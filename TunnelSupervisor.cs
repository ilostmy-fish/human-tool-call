using System.Diagnostics;
using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace HumanToolCall;

internal enum TunnelState
{
    Off,
    On,
    Error
}

internal sealed class TunnelStatus
{
    internal TunnelState State { get; init; }
    internal string Details { get; init; } = string.Empty;
    internal IReadOnlyList<int> Pids { get; init; } = Array.Empty<int>();
}

internal sealed class TunnelSupervisor : IDisposable
{
    private readonly TunnelConfig _config;
    private readonly HttpClient _healthClient;

    internal TunnelSupervisor(TunnelConfig config)
    {
        _config = config;
        HttpClientHandler handler = new() { UseProxy = false };
        _healthClient = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    internal TunnelStatus Status(bool reconcile = true)
    {
        if (reconcile)
        {
            Reconcile();
        }

        return DetermineStatus(Observe());
    }

    internal TunnelStatus Start()
    {
        Reconcile();
        TunnelObservation observation = Observe();
        TunnelStatus before = DetermineStatus(observation);

        if (!string.IsNullOrEmpty(observation.QueryError) || observation.UnclassifiedPids.Count > 0)
        {
            return before;
        }

        if (observation.Exact.Count > 0 || observation.Malformed.Count > 0)
        {
            return before;
        }

        if (!File.Exists(_config.ExecutablePath))
        {
            return Error($"Tunnel client not found: {_config.ExecutablePath}");
        }

        string apiKey;
        try
        {
            apiKey = LoadApiKey();
        }
        catch (Exception ex)
        {
            return Error($"Could not load CONTROL_PLANE_API_KEY. {ex.GetType().Name}: {ex.Message}");
        }

        try
        {
            ProcessStartInfo startInfo = new()
            {
                FileName = _config.ExecutablePath,
                WorkingDirectory = Path.GetDirectoryName(_config.ExecutablePath) ?? AppContext.BaseDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            startInfo.ArgumentList.Add("run");
            startInfo.ArgumentList.Add("--profile");
            startInfo.ArgumentList.Add(_config.Profile);
            startInfo.ArgumentList.Add("--health.listen-addr");
            startInfo.ArgumentList.Add(_config.HealthListenAddress);
            startInfo.Environment[_config.ControlPlaneApiKeyEnvironmentVariable] = apiKey;

            using Process? process = Process.Start(startInfo);
            if (process is null)
            {
                return Error("Windows did not return a tunnel-client process.");
            }
        }
        catch (Exception ex)
        {
            return Error($"Could not start tunnel-client. {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            apiKey = string.Empty;
        }

        Thread.Sleep(_config.PostStartDelayMs);
        return DetermineStatus(Observe());
    }

    internal TunnelStatus Stop()
    {
        Reconcile();
        TunnelObservation observation = Observe();

        foreach (ManagedTunnelProcess process in observation.Exact.Concat(observation.Malformed))
        {
            try
            {
                KillProcess(process.Pid);
            }
            catch (Exception ex)
            {
                return Error($"Could not stop tunnel process PID {process.Pid}. {ex.GetType().Name}: {ex.Message}", [process.Pid]);
            }
        }

        return DetermineStatus(Observe());
    }

    private void Reconcile()
    {
        TunnelObservation observation = Observe();
        foreach (ManagedTunnelProcess process in observation.Malformed)
        {
            TryKill(process.Pid);
        }

        if (observation.Exact.Count > 1)
        {
            foreach (ManagedTunnelProcess process in observation.Exact)
            {
                TryKill(process.Pid);
            }
        }
    }

    private TunnelObservation Observe()
    {
        TunnelObservation observation = new();
        HashSet<int> seenPids = new();

        try
        {
            ConnectionOptions connectionOptions = new()
            {
                Timeout = TimeSpan.FromMilliseconds(_config.ProcessQueryTimeoutMs)
            };
            ManagementScope scope = new(@"\\.\root\cimv2", connectionOptions);
            ObjectQuery query = new("SELECT ProcessId, ExecutablePath, CommandLine FROM Win32_Process WHERE Name='tunnel-client.exe'");
            EnumerationOptions options = new()
            {
                ReturnImmediately = true,
                Rewindable = false,
                Timeout = TimeSpan.FromMilliseconds(_config.ProcessQueryTimeoutMs)
            };

            using ManagementObjectSearcher searcher = new(scope, query, options);
            using ManagementObjectCollection processes = searcher.Get();
            foreach (ManagementObject process in processes)
            {
                using (process)
                {
                    int pid = ReadProcessId(process);
                    if (pid <= 0 || !seenPids.Add(pid))
                    {
                        continue;
                    }

                    string? executablePath = ReadManagementString(process, "ExecutablePath");
                    if (string.IsNullOrWhiteSpace(executablePath))
                    {
                        observation.UnclassifiedPids.Add(pid);
                        continue;
                    }

                    if (!PathsEqual(executablePath, _config.ExecutablePath))
                    {
                        continue;
                    }

                    string? commandLine = ReadManagementString(process, "CommandLine");
                    if (string.IsNullOrWhiteSpace(commandLine) || !TryParseCommandLine(commandLine, out string[] args))
                    {
                        observation.UnclassifiedPids.Add(pid);
                        continue;
                    }

                    TunnelInvocation invocation = ParseInvocation(args);
                    if (!invocation.IsRun)
                    {
                        continue;
                    }

                    if (!invocation.ProfileIsClassifiable)
                    {
                        observation.UnclassifiedPids.Add(pid);
                        continue;
                    }

                    if (!string.Equals(invocation.Profile, _config.Profile, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    ManagedTunnelProcess managed = new(pid, invocation);
                    if (IsExact(invocation))
                    {
                        observation.Exact.Add(managed);
                    }
                    else
                    {
                        observation.Malformed.Add(managed);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            observation.QueryError = $"Tunnel process query failed. {ex.GetType().Name}: {ex.Message}";
        }

        return observation;
    }

    private TunnelStatus DetermineStatus(TunnelObservation observation)
    {
        if (!string.IsNullOrEmpty(observation.QueryError))
        {
            return Error(observation.QueryError, observation.AllPids());
        }

        if (observation.UnclassifiedPids.Count > 0)
        {
            return Error(
                "One or more tunnel-client.exe processes could not be safely classified: " +
                string.Join(", ", observation.UnclassifiedPids),
                observation.AllPids());
        }

        if (observation.Malformed.Count > 0)
        {
            return Error("A noncanonical tunnel-client invocation remains for the configured profile.", observation.AllPids());
        }

        if (observation.Exact.Count > 1)
        {
            return Error("Duplicate canonical tunnel-client processes remain.", observation.AllPids());
        }

        if (observation.Exact.Count == 0)
        {
            return new TunnelStatus { State = TunnelState.Off, Details = "Tunnel is not running." };
        }

        (bool success, string details) = ProbeHealth();
        return success
            ? new TunnelStatus
            {
                State = TunnelState.On,
                Details = "Tunnel process and health endpoint are live.",
                Pids = [observation.Exact[0].Pid]
            }
            : Error(details, [observation.Exact[0].Pid]);
    }

    private (bool Success, string Details) ProbeHealth()
    {
        try
        {
            using CancellationTokenSource timeout = new(_config.HealthTimeoutMs);
            using HttpRequestMessage request = new(HttpMethod.Get, _config.HealthUrl);
            using HttpResponseMessage response = _healthClient.Send(request, HttpCompletionOption.ResponseContentRead, timeout.Token);
            string body = response.Content.ReadAsStringAsync(timeout.Token).GetAwaiter().GetResult();

            if (!response.IsSuccessStatusCode)
            {
                return (false, $"Tunnel health returned HTTP {(int)response.StatusCode}.");
            }

            if (!string.Equals(body.Trim(), "live", StringComparison.OrdinalIgnoreCase))
            {
                return (false, "Tunnel health returned an unexpected response body.");
            }

            return (true, string.Empty);
        }
        catch (OperationCanceledException)
        {
            return (false, $"Tunnel health timed out after {_config.HealthTimeoutMs} ms.");
        }
        catch (Exception ex)
        {
            return (false, $"Tunnel health failed. {ex.GetType().Name}: {ex.Message}");
        }
    }

    private bool IsExact(TunnelInvocation invocation) =>
        !invocation.ProfileMalformed &&
        !invocation.HealthMalformed &&
        !invocation.HasUnexpectedArguments &&
        invocation.HealthWasSpecified &&
        string.Equals(invocation.HealthListenAddress, _config.HealthListenAddress, StringComparison.OrdinalIgnoreCase);

    private static TunnelInvocation ParseInvocation(string[] args)
    {
        TunnelInvocation invocation = new();
        if (args.Length < 2 || !string.Equals(args[1], "run", StringComparison.OrdinalIgnoreCase))
        {
            return invocation;
        }

        invocation.IsRun = true;
        bool profileSeen = false;
        bool healthSeen = false;

        for (int i = 2; i < args.Length; i++)
        {
            string arg = args[i] ?? string.Empty;
            if (string.Equals(arg, "--profile", StringComparison.OrdinalIgnoreCase))
            {
                if (profileSeen || i + 1 >= args.Length)
                {
                    invocation.ProfileMalformed = true;
                    continue;
                }
                profileSeen = true;
                invocation.Profile = args[++i];
                invocation.ProfileMalformed |= string.IsNullOrWhiteSpace(invocation.Profile);
                continue;
            }

            const string profilePrefix = "--profile=";
            if (arg.StartsWith(profilePrefix, StringComparison.OrdinalIgnoreCase))
            {
                if (profileSeen)
                {
                    invocation.ProfileMalformed = true;
                    continue;
                }
                profileSeen = true;
                invocation.Profile = arg[profilePrefix.Length..];
                invocation.ProfileMalformed |= string.IsNullOrWhiteSpace(invocation.Profile);
                continue;
            }

            if (string.Equals(arg, "--health.listen-addr", StringComparison.OrdinalIgnoreCase))
            {
                if (healthSeen || i + 1 >= args.Length)
                {
                    invocation.HealthMalformed = true;
                    continue;
                }
                healthSeen = true;
                invocation.HealthWasSpecified = true;
                invocation.HealthListenAddress = args[++i];
                invocation.HealthMalformed |= string.IsNullOrWhiteSpace(invocation.HealthListenAddress);
                continue;
            }

            const string healthPrefix = "--health.listen-addr=";
            if (arg.StartsWith(healthPrefix, StringComparison.OrdinalIgnoreCase))
            {
                if (healthSeen)
                {
                    invocation.HealthMalformed = true;
                    continue;
                }
                healthSeen = true;
                invocation.HealthWasSpecified = true;
                invocation.HealthListenAddress = arg[healthPrefix.Length..];
                invocation.HealthMalformed |= string.IsNullOrWhiteSpace(invocation.HealthListenAddress);
                continue;
            }

            invocation.HasUnexpectedArguments = true;
        }

        if (!profileSeen)
        {
            invocation.ProfileMalformed = true;
        }

        invocation.ProfileIsClassifiable = profileSeen && !invocation.ProfileMalformed && !string.IsNullOrWhiteSpace(invocation.Profile);
        return invocation;
    }

    private string LoadApiKey()
    {
        if (!string.IsNullOrWhiteSpace(_config.ControlPlaneApiKeyFile))
        {
            if (!File.Exists(_config.ControlPlaneApiKeyFile))
            {
                throw new FileNotFoundException("Encrypted API key file not found.", _config.ControlPlaneApiKeyFile);
            }
            return ReadPowerShellDpapiSecureString(_config.ControlPlaneApiKeyFile);
        }

        string? value = Environment.GetEnvironmentVariable(_config.ControlPlaneApiKeyEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(
                $"Neither tunnel.controlPlaneApiKeyFile nor environment variable {_config.ControlPlaneApiKeyEnvironmentVariable} supplied a key.");
        }
        return value;
    }

    private static string ReadPowerShellDpapiSecureString(string path)
    {
        string input = File.ReadAllText(path);
        StringBuilder clean = new(input.Length);
        foreach (char c in input)
        {
            if (!char.IsWhiteSpace(c)) clean.Append(c);
        }

        if (clean.Length == 0 || clean.Length % 2 != 0)
        {
            throw new InvalidDataException("The DPAPI key file is empty or is not the expected PowerShell ConvertFrom-SecureString format.");
        }

        byte[] encrypted = new byte[clean.Length / 2];
        try
        {
            for (int i = 0; i < encrypted.Length; i++)
            {
                int high = HexValue(clean[i * 2]);
                int low = HexValue(clean[i * 2 + 1]);
                if (high < 0 || low < 0)
                {
                    throw new InvalidDataException("The DPAPI key file is not hexadecimal.");
                }
                encrypted[i] = (byte)((high << 4) | low);
            }

            byte[] plain = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
            try
            {
                string value = Encoding.Unicode.GetString(plain).TrimEnd('\0');
                if (string.IsNullOrWhiteSpace(value)) throw new InvalidDataException("The decrypted API key is empty.");
                return value;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plain);
            }
        }
        catch (CryptographicException ex)
        {
            throw new InvalidOperationException(
                "Windows could not decrypt the API key. Run as the same Windows user on the same machine that created the DPAPI file.", ex);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encrypted);
        }
    }

    private void TryKill(int pid)
    {
        try { KillProcess(pid); } catch { }
    }

    private void KillProcess(int pid)
    {
        try
        {
            using Process process = Process.GetProcessById(pid);
            if (process.HasExited) return;
            process.Kill(true);
            if (!process.WaitForExit(_config.ProcessExitTimeoutMs))
            {
                throw new TimeoutException($"Process did not exit within {_config.ProcessExitTimeoutMs} ms.");
            }
        }
        catch (ArgumentException)
        {
            // Process exited after observation.
        }
    }

    private static TunnelStatus Error(string details, IReadOnlyList<int>? pids = null) => new()
    {
        State = TunnelState.Error,
        Details = details,
        Pids = pids ?? Array.Empty<int>()
    };

    private static int ReadProcessId(ManagementObject process)
    {
        try
        {
            object? value = process["ProcessId"];
            if (value is null) return 0;
            uint pid = Convert.ToUInt32(value, CultureInfo.InvariantCulture);
            return pid <= int.MaxValue ? (int)pid : 0;
        }
        catch { return 0; }
    }

    private static string? ReadManagementString(ManagementObject process, string propertyName)
    {
        try
        {
            object? value = process[propertyName];
            return value is null ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
        }
        catch { return null; }
    }

    private static bool PathsEqual(string left, string right)
    {
        try
        {
            string a = Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string b = Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static bool TryParseCommandLine(string commandLine, out string[] args)
    {
        args = Array.Empty<string>();
        IntPtr argv = CommandLineToArgvW(commandLine, out int argc);
        if (argv == IntPtr.Zero || argc <= 0) return false;

        try
        {
            string[] parsed = new string[argc];
            for (int i = 0; i < argc; i++)
            {
                IntPtr argPtr = Marshal.ReadIntPtr(argv, i * IntPtr.Size);
                parsed[i] = Marshal.PtrToStringUni(argPtr) ?? string.Empty;
            }
            args = parsed;
            return true;
        }
        finally
        {
            LocalFree(argv);
        }
    }

    private static int HexValue(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        >= 'A' and <= 'F' => c - 'A' + 10,
        _ => -1
    };

    public void Dispose() => _healthClient.Dispose();

    [DllImport("shell32.dll", SetLastError = true)]
    private static extern IntPtr CommandLineToArgvW([MarshalAs(UnmanagedType.LPWStr)] string lpCmdLine, out int pNumArgs);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);

    private sealed class TunnelObservation
    {
        internal string? QueryError { get; set; }
        internal List<int> UnclassifiedPids { get; } = new();
        internal List<ManagedTunnelProcess> Exact { get; } = new();
        internal List<ManagedTunnelProcess> Malformed { get; } = new();
        internal IReadOnlyList<int> AllPids() => Exact.Select(x => x.Pid)
            .Concat(Malformed.Select(x => x.Pid))
            .Concat(UnclassifiedPids)
            .Distinct()
            .OrderBy(x => x)
            .ToArray();
    }

    private sealed record ManagedTunnelProcess(int Pid, TunnelInvocation Invocation);

    private sealed class TunnelInvocation
    {
        internal bool IsRun { get; set; }
        internal string? Profile { get; set; }
        internal bool ProfileMalformed { get; set; }
        internal bool ProfileIsClassifiable { get; set; }
        internal bool HealthWasSpecified { get; set; }
        internal string? HealthListenAddress { get; set; }
        internal bool HealthMalformed { get; set; }
        internal bool HasUnexpectedArguments { get; set; }
    }
}
