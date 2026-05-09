using System.Text.Json;

namespace picoNET.agent;

/// <summary>
/// Agent configuration loaded from environment variables, config file, or command-line overrides.
/// </summary>
internal class AgentConfig
{
    public string BaseUrl { get; set; } = "http://100.118.58.55:8020/v1";
    public string? ApiKey { get; set; }
    public string Model { get; set; } = "qwen3.6-27b-autoround";
    public int? MaxTokens { get; set; }
    public float? Temperature { get; set; }
    public string[]? Tools { get; set; }
    public bool? ThinkingEnabled { get; set; } = true;
    public Dictionary<string, McpServerConfig> McpServers { get; set; } = new();
    public Dictionary<string, string> Values { get; set; } = new();

    private const string ConfigFileName = "pico-agent.json";
    private const string ConfigDirName = ".pico";

    internal static string ConfigPath
    {
        get
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var configDir = Path.Combine(home, ConfigDirName);
            return Path.Combine(configDir, ConfigFileName);
        }
    }

    /// <summary>
    /// Build config from optional overrides (command-line takes priority).
    /// </summary>
    public static AgentConfig Build(string? baseUrl, string? apiKey, string? model, float? temperature = null, int? maxTokens = null)
    {
        var config = Load();

        if (!string.IsNullOrEmpty(baseUrl))
            config.BaseUrl = baseUrl.TrimEnd('/');
        if (!string.IsNullOrEmpty(apiKey))
            config.ApiKey = apiKey;
        if (!string.IsNullOrEmpty(model))
            config.Model = model;
        if (temperature.HasValue)
            config.Temperature = temperature;
        if (maxTokens.HasValue)
            config.MaxTokens = maxTokens;

        return config;
    }

    /// <summary>
    /// Load config from file, then environment variables (env takes priority).
    /// </summary>
    public static AgentConfig Load()
    {
        var config = new AgentConfig();

        // Load from file first
        if (File.Exists(ConfigPath))
        {
            try
            {
                var json = File.ReadAllText(ConfigPath);
                var loaded = JsonSerializer.Deserialize<AgentConfig>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
                if (loaded != null)
                {
                    config.BaseUrl = loaded.BaseUrl;
                    config.ApiKey = loaded.ApiKey;
                    config.Model = loaded.Model;
                    config.MaxTokens = loaded.MaxTokens;
                    config.Temperature = loaded.Temperature;
                    config.Tools = loaded.Tools;
                    config.ThinkingEnabled = loaded.ThinkingEnabled;
                    config.McpServers = loaded.McpServers ?? new();
                    config.Values = loaded.Values ?? new();
                }
            }
            catch
            {
                // Ignore parse errors
            }
        }

        ApplyValues(config);

        // Environment variables override everything
        if (Environment.GetEnvironmentVariable("AGENT_BASE_URL") is { Length: > 0 } envBase)
            config.BaseUrl = envBase.TrimEnd('/');
        if (Environment.GetEnvironmentVariable("AGENT_API_KEY") is { Length: > 0 } envKey)
            config.ApiKey = envKey;
        if (Environment.GetEnvironmentVariable("AGENT_MODEL") is { Length: > 0 } envModel)
            config.Model = envModel;
        if (int.TryParse(Environment.GetEnvironmentVariable("AGENT_MAX_TOKENS"), out var envMax))
            config.MaxTokens = envMax;
        if (float.TryParse(Environment.GetEnvironmentVariable("AGENT_TEMPERATURE"), out var envTemp))
            config.Temperature = envTemp;
        if (Environment.GetEnvironmentVariable("AGENT_TOOLS") is { Length: > 0 } envTools)
            config.Tools = envTools.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (bool.TryParse(Environment.GetEnvironmentVariable("AGENT_THINKING_ENABLED"), out var envThinking))
            config.ThinkingEnabled = envThinking;

        return config;
    }

    public void ApplyValue(string key, string value)
    {
        ApplyValue(this, key, value);
    }

    private static void ApplyValues(AgentConfig config)
    {
        foreach (var (key, value) in config.Values)
            ApplyValue(config, key, value);
    }

    private static void ApplyValue(AgentConfig config, string key, string value)
    {
        switch (key.Trim().ToUpperInvariant())
        {
            case "BASE_URL":
            case "AGENT_BASE_URL":
                config.BaseUrl = value.TrimEnd('/');
                break;
            case "API_KEY":
            case "AGENT_API_KEY":
                config.ApiKey = value;
                break;
            case "MODEL":
            case "AGENT_MODEL":
                config.Model = value;
                break;
            case "MAX_TOKENS":
            case "AGENT_MAX_TOKENS":
                if (int.TryParse(value, out var maxTokens))
                    config.MaxTokens = maxTokens;
                break;
            case "TEMPERATURE":
            case "AGENT_TEMPERATURE":
                if (float.TryParse(value, out var temperature))
                    config.Temperature = temperature;
                break;
            case "THINKING_ENABLED":
            case "AGENT_THINKING_ENABLED":
                if (bool.TryParse(value, out var thinkingEnabled))
                    config.ThinkingEnabled = thinkingEnabled;
                break;
            case "TOOLS":
            case "AGENT_TOOLS":
                config.Tools = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                break;
        }
    }

    public void Save()
    {
        var dir = Path.GetDirectoryName(ConfigPath)!;
        if (!Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        });

        File.WriteAllText(ConfigPath, json);
    }
}

internal class McpServerConfig
{
    public string Command { get; set; } = string.Empty;
    public string[] Args { get; set; } = Array.Empty<string>();
    public Dictionary<string, string>? Env { get; set; }
    public Dictionary<string, string>? Headers { get; set; }
    public string Type { get; set; } = "stdio";
    public string? Url { get; set; }
    public bool Disabled { get; set; }
}
