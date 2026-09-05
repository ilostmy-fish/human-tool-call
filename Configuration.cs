using System.Text.Json;
using System.Text.Json.Serialization;

namespace HumanToolCall;

internal enum ConfigPathMode
{
    ExecutableDirectory,
    Documents,
    LocalAppData
}

internal static class ConfigPathPolicy
{
    // Change this before building if you want the executable to look elsewhere.
    internal const ConfigPathMode Location = ConfigPathMode.ExecutableDirectory;
    internal const string FileName = "human-tool-call.json";

    internal static string Resolve()
    {
        string directory = Location switch
        {
            ConfigPathMode.Documents => Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "HumanToolCall"),
            ConfigPathMode.LocalAppData => Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "HumanToolCall"),
            _ => AppContext.BaseDirectory
        };

        Directory.CreateDirectory(directory);
        return Path.Combine(directory, FileName);
    }
}

internal sealed class AppConfig
{
    public bool StartWithWindows { get; set; }
    public bool StartBackendOnLaunch { get; set; }
    public bool StartTunnelOnLaunch { get; set; }
    public bool OpenBrowserOnLaunch { get; set; }
    public bool StopTunnelOnExit { get; set; } = true;
    public bool NotifyOnQuestions { get; set; } = true;
    public bool NotifyOnProgressReports { get; set; }
    public BackendConfig Backend { get; set; } = new();
    public TunnelConfig Tunnel { get; set; } = new();
}

internal sealed class BackendConfig
{
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 64500;
    public string McpPath { get; set; } = "/mcp";
    public int InteractionTimeoutSeconds { get; set; } = 600;
    public int BrowserLongPollSeconds { get; set; } = 25;
    public int MaxPendingInteractions { get; set; } = 16;
    public int MaxQuestionsPerInteraction { get; set; } = 20;
    public int MaxRecentProgressReports { get; set; } = 20;
}

internal sealed class TunnelConfig
{
    public string ExecutablePath { get; set; } = @"C:\tools\tunnel-client\tunnel-client.exe";
    public string Profile { get; set; } = "human-tool-call";
    public string HealthHost { get; set; } = "127.0.0.1";
    public int HealthPort { get; set; } = 8082;
    public int ProcessQueryTimeoutMs { get; set; } = 400;
    public int HealthTimeoutMs { get; set; } = 400;
    public int ProcessExitTimeoutMs { get; set; } = 2000;
    public int PostStartDelayMs { get; set; } = 350;
    public string ControlPlaneApiKeyFile { get; set; } = @"C:\tools\tunnel-client\control-plane-api-key.dpapi";
    public string ControlPlaneApiKeyEnvironmentVariable { get; set; } = "CONTROL_PLANE_API_KEY";

    [JsonIgnore]
    public string HealthListenAddress => $"{HealthHost}:{HealthPort}";

    [JsonIgnore]
    public string HealthUrl => $"http://{HealthListenAddress}/healthz";
}

internal static class ConfigLoader
{
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    internal static (AppConfig Config, string Path, bool Created) LoadOrCreate()
    {
        string path = ConfigPathPolicy.Resolve();
        bool created = false;

        if (!File.Exists(path))
        {
            File.WriteAllText(path, JsonSerializer.Serialize(new AppConfig(), JsonOptions));
            created = true;
        }

        AppConfig? config = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path), JsonOptions);
        if (config is null)
        {
            throw new InvalidDataException("The configuration file could not be parsed.");
        }

        NormalizeAndValidate(config, path);
        return (config, path, created);
    }

    private static void NormalizeAndValidate(AppConfig config, string configPath)
    {
        config.Backend ??= new BackendConfig();
        config.Tunnel ??= new TunnelConfig();

        if (!string.Equals(config.Backend.Host, "127.0.0.1", StringComparison.Ordinal))
        {
            throw new InvalidDataException("backend.host must be 127.0.0.1. Human Tool Call intentionally binds only to loopback.");
        }

        ValidatePort(config.Backend.Port, "backend.port");
        ValidatePort(config.Tunnel.HealthPort, "tunnel.healthPort");

        if (string.IsNullOrWhiteSpace(config.Backend.McpPath))
        {
            config.Backend.McpPath = "/mcp";
        }
        else if (!config.Backend.McpPath.StartsWith('/'))
        {
            config.Backend.McpPath = "/" + config.Backend.McpPath;
        }

        if (config.Backend.InteractionTimeoutSeconds < 15)
        {
            throw new InvalidDataException("backend.interactionTimeoutSeconds must be at least 15.");
        }

        if (config.Backend.BrowserLongPollSeconds is < 5 or > 55)
        {
            throw new InvalidDataException("backend.browserLongPollSeconds must be between 5 and 55.");
        }

        if (config.Backend.MaxPendingInteractions is < 1 or > 128)
        {
            throw new InvalidDataException("backend.maxPendingInteractions must be between 1 and 128.");
        }

        if (config.Backend.MaxQuestionsPerInteraction is < 1 or > 100)
        {
            throw new InvalidDataException("backend.maxQuestionsPerInteraction must be between 1 and 100.");
        }

        if (config.Backend.MaxRecentProgressReports is < 1 or > 200)
        {
            throw new InvalidDataException("backend.maxRecentProgressReports must be between 1 and 200.");
        }

        if (string.IsNullOrWhiteSpace(config.Tunnel.Profile))
        {
            throw new InvalidDataException("tunnel.profile is required.");
        }

        if (!string.Equals(config.Tunnel.HealthHost, "127.0.0.1", StringComparison.Ordinal))
        {
            throw new InvalidDataException("tunnel.healthHost must be 127.0.0.1.");
        }

        string configDirectory = Path.GetDirectoryName(configPath) ?? AppContext.BaseDirectory;
        config.Tunnel.ExecutablePath = ResolveConfiguredPath(config.Tunnel.ExecutablePath, configDirectory);
        config.Tunnel.ControlPlaneApiKeyFile = ResolveConfiguredPath(config.Tunnel.ControlPlaneApiKeyFile, configDirectory);

        if (config.StartTunnelOnLaunch && !config.StartBackendOnLaunch)
        {
            throw new InvalidDataException("startTunnelOnLaunch requires startBackendOnLaunch because this tunnel targets the local MCP backend.");
        }
    }

    private static string ResolveConfiguredPath(string value, string baseDirectory)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        string expanded = Environment.ExpandEnvironmentVariables(value.Trim());
        return Path.IsPathRooted(expanded)
            ? Path.GetFullPath(expanded)
            : Path.GetFullPath(Path.Combine(baseDirectory, expanded));
    }

    private static void ValidatePort(int port, string name)
    {
        if (port is < 1 or > 65535)
        {
            throw new InvalidDataException($"{name} must be between 1 and 65535.");
        }
    }
}
