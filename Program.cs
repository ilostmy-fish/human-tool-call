using Microsoft.Win32;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using System.Reflection;
using System.Text.Json;
using System.Windows.Forms;

namespace HumanToolCall;

internal static class Program
{
    private const string MutexName = @"Local\HumanToolCall";

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 1 && string.Equals(args[0], "--dump-tools", StringComparison.Ordinal))
        {
            DumpTools();
            return 0;
        }

        using Mutex mutex = new(true, MutexName, out bool createdNew);
        if (!createdNew)
        {
            return 0;
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        AppConfig config;
        string configPath;
        try
        {
            (config, configPath, _) = ConfigLoader.LoadOrCreate();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "HumanToolCall could not load its configuration.\n\n" + ex.Message,
                "HumanToolCall",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return 1;
        }

        try
        {
            StartupManager.Apply(config.StartWithWindows);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"HumanToolCall loaded {configPath}, but could not update Windows startup registration.\n\n{ex.Message}",
                "HumanToolCall",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }

        InteractionBroker broker = new(config.Backend);
        BackendService backend = new(config.Backend, broker);
        using TunnelSupervisor tunnel = new(config.Tunnel);

        try
        {
            if (config.StartBackendOnLaunch)
            {
                try
                {
                    backend.StartAsync().GetAwaiter().GetResult();
                }
                catch
                {
                    // The tray will show Backend: Off. The user can retry manually.
                }
            }

            if (config.StartTunnelOnLaunch && backend.IsRunning)
            {
                try
                {
                    tunnel.Start();
                }
                catch
                {
                    // Status reconciliation in the tray will expose the resulting state.
                }
            }

            using TrayApplicationContext context = new(config, backend, tunnel, broker);
            if (config.OpenBrowserOnLaunch && backend.IsRunning)
            {
                context.OpenBrowser();
            }

            Application.Run(context);
        }
        finally
        {
            if (config.StopTunnelOnExit)
            {
                try
                {
                    tunnel.Stop();
                }
                catch
                {
                }
            }

            try
            {
                backend.StopAsync().GetAwaiter().GetResult();
            }
            catch
            {
            }

            try
            {
                backend.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            catch
            {
            }
        }

        return 0;
    }

    private static void DumpTools()
    {
        UserCommunicationTools target = new(new InteractionBroker(new BackendConfig()));
        Tool[] tools = typeof(UserCommunicationTools)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
            .Where(method => method.GetCustomAttribute<McpServerToolAttribute>() is not null)
            .OrderBy(method => method.MetadataToken)
            .Select(method => McpServerTool.Create(method, target).ProtocolTool)
            .ToArray();

        string outputDirectory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "testOutputs"));
        Directory.CreateDirectory(outputDirectory);

        ListToolsResult list = new() { Tools = tools };
        File.WriteAllText(
            Path.Combine(outputDirectory, "tools.json"),
            JsonSerializer.Serialize(list, McpJsonUtilities.DefaultOptions));

        foreach (Tool tool in tools)
        {
            File.WriteAllText(
                Path.Combine(outputDirectory, $"{tool.Name}.json"),
                JsonSerializer.Serialize(tool, McpJsonUtilities.DefaultOptions));
        }
    }
}

internal static class StartupManager
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "HumanToolCall";

    internal static void Apply(bool enabled)
    {
        using RegistryKey? key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
        if (key is null)
        {
            throw new InvalidOperationException("Could not open the current user's Windows Run registry key.");
        }

        if (!enabled)
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
            return;
        }

        string? executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable))
        {
            throw new InvalidOperationException("Windows did not report the current executable path.");
        }

        key.SetValue(ValueName, $"\"{executable}\"", RegistryValueKind.String);
    }
}