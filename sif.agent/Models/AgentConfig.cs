using System.Text.Json;
using System.Text.Json.Serialization;
using sif.agent.Services;

namespace sif.agent;

/// <summary>
/// Agent configuration loaded from environment variables, config file, or command-line overrides.
/// </summary>
internal class AgentConfig
{
    public const int DefaultCompactionThreshold = 100000;

    public string BaseUrl { get; set; } = "http://localhost:1234/v1";
    public string? ApiKey { get; set; }
    public string Model { get; set; } = "qwen3.6-27b-autoround";
    public int? MaxTokens { get; set; }
    public float? Temperature { get; set; }
    public int? ModelTimeoutSeconds { get; set; }
    public string[]? Tools { get; set; }
    public string[]? ShellAllowedCommands { get; set; }
    public bool? ThinkingEnabled { get; set; } = true;
    /// <summary>
    /// If true, the API key is stored in the OS secure credential store
    /// instead of plaintext in the config file.
    /// </summary>
    public bool UseSecureApiKeyStorage { get; set; }
    /// <summary>
    /// If true, the app checks for tool updates on startup and offers to install them.
    /// </summary>
    public bool AutoUpdateEnabled { get; set; }
    /// <summary>
    /// Optional source to use when updating the global tool.
    /// If empty, dotnet tool update uses the default registered source(s).
    /// </summary>
    public string? AutoUpdateSource { get; set; }
    /// <summary>
    /// Named provider configurations (API endpoints) that can be shared across multiple models.
    /// </summary>
    public Dictionary<string, ProviderConfig> Providers { get; set; } = new();
    /// <summary>
    /// Named model profiles for easy switching between models.
    /// Each profile references a provider by name via <see cref="ModelProfile.Provider"/>.
    /// </summary>
    public Dictionary<string, ModelProfile> Profiles { get; set; } = new();
    /// <summary>
    /// Name of the currently active model profile. If null or empty, the flat
    /// properties (BaseUrl, Model, etc.) are used instead.
    /// </summary>
    public string? CurrentProfile { get; set; }
    /// <summary>
    /// Token threshold at which the chat history is compacted (summarized) via the LLM.
    /// Default is 60000 tokens (~240k chars), roughly 60% of a 100k context window.
    /// Set to 0 to disable compaction.
    /// </summary>
    public int CompactionThreshold { get; set; } = DefaultCompactionThreshold;
    [JsonIgnore]
    public bool CompactionThresholdConfigured { get; set; }
    public Dictionary<string, McpServerConfig> McpServers { get; set; } = new();
    public Dictionary<string, string> Values { get; set; } = new();

    private const string ConfigFileName = "sif-agent.json";
    private const string ConfigDirName = ".sif";

    internal static string ConfigPath
    {
        get
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var configDir = Path.Combine(home, ConfigDirName);
            return Path.Combine(configDir, ConfigFileName);
        }
    }

    internal static bool ConfigFileExists => File.Exists(ConfigPath);

    /// <summary>
    /// Build config from optional overrides (command-line takes priority).
    /// </summary>
    public static AgentConfig Build(string? baseUrl, string? apiKey, string? model, float? temperature = null, int? maxTokens = null, int? modelTimeoutSeconds = null)
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
        if (modelTimeoutSeconds.HasValue)
            config.ModelTimeoutSeconds = modelTimeoutSeconds;

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
                    config.ModelTimeoutSeconds = loaded.ModelTimeoutSeconds;
                    config.Tools = loaded.Tools;
                    config.ShellAllowedCommands = loaded.ShellAllowedCommands;
                    config.ThinkingEnabled = loaded.ThinkingEnabled;
                    config.CompactionThreshold = loaded.CompactionThreshold;
                    config.CompactionThresholdConfigured = loaded.CompactionThreshold != DefaultCompactionThreshold;
                    config.UseSecureApiKeyStorage = loaded.UseSecureApiKeyStorage;
                    config.AutoUpdateEnabled = loaded.AutoUpdateEnabled;
                    config.AutoUpdateSource = loaded.AutoUpdateSource;
                    config.McpServers = loaded.McpServers ?? new();
                    config.Values = loaded.Values ?? new();
                    config.Providers = loaded.Providers ?? new();
                    config.Profiles = loaded.Profiles ?? new();
                    config.CurrentProfile = loaded.CurrentProfile;

                    if (NormalizeProfiles(config))
                        config.Save();
                }
            }
            catch
            {
                // Ignore parse errors
            }
        }

        ApplyValues(config);

        // Switch to the current profile so flat properties reflect it
        // (Environment variables below will still override if set)
        if (!string.IsNullOrEmpty(config.CurrentProfile) && config.Profiles.ContainsKey(config.CurrentProfile))
        {
            config.SwitchProfile(config.CurrentProfile);
        }

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
        if (int.TryParse(Environment.GetEnvironmentVariable("AGENT_MODEL_TIMEOUT_SECONDS"), out var envModelTimeout))
            config.ModelTimeoutSeconds = envModelTimeout;
        if (Environment.GetEnvironmentVariable("AGENT_TOOLS") is { Length: > 0 } envTools)
            config.Tools = envTools.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (bool.TryParse(Environment.GetEnvironmentVariable("AGENT_THINKING_ENABLED"), out var envThinking))
            config.ThinkingEnabled = envThinking;
        var envCompactionThreshold =
            Environment.GetEnvironmentVariable("AGENT_COMPACTION_THRESHOLD") ??
            Environment.GetEnvironmentVariable("AGENT_COMPACT_THRESHOLD");
        if (int.TryParse(envCompactionThreshold, out var envCompact))
        {
            config.CompactionThreshold = envCompact;
            config.CompactionThresholdConfigured = true;
        }
        if (bool.TryParse(Environment.GetEnvironmentVariable("AGENT_AUTO_UPDATE_ENABLED"), out var envAutoUpdateEnabled))
            config.AutoUpdateEnabled = envAutoUpdateEnabled;
        if (Environment.GetEnvironmentVariable("AGENT_AUTO_UPDATE_SOURCE") is { Length: > 0 } envAutoUpdateSource)
            config.AutoUpdateSource = envAutoUpdateSource;

        // Load API key from secure storage if configured
        if (config.UseSecureApiKeyStorage && string.IsNullOrEmpty(config.ApiKey))
        {
            var credentialStore = SecureCredentialStoreFactory.Create();
            var secureKey = credentialStore.RetrieveAsync($"api-key-{config.CurrentProfile}").GetAwaiter().GetResult();
            if (!string.IsNullOrEmpty(secureKey))
            {
                config.ApiKey = secureKey;
            }
        }

        return config;
    }

    /// <summary>
    /// Get the active model profile. If a current profile is set, returns it.
    /// Otherwise returns a profile built from the flat config properties.
    /// </summary>
    public ModelProfile? GetActiveProfile()
    {
        if (!string.IsNullOrEmpty(CurrentProfile) && Profiles.TryGetValue(CurrentProfile, out var profile))
        {
            // If the profile's provider uses secure storage, load it now
            ProviderConfig? provider = null;
            if (!string.IsNullOrEmpty(profile.Provider) && Providers.TryGetValue(profile.Provider, out var p))
            {
                provider = p;
            }
            LoadProviderApiKeyFromSecureStorage(profile, provider);
            return profile;
        }

        // Fall back to flat config
        return new ModelProfile
        {
            Name = CurrentProfile ?? "default",
            Provider = null,
            BaseUrl = BaseUrl,
            ApiKey = ApiKey,
            Model = Model,
            Temperature = Temperature,
            MaxTokens = MaxTokens,
            ModelTimeoutSeconds = ModelTimeoutSeconds,
            UseSecureApiKeyStorage = UseSecureApiKeyStorage,
            ThinkingEnabled = ThinkingEnabled ?? true
        };
    }

    /// <summary>
    /// Switch to a named profile and update flat properties to match.
    /// Resolves provider settings from <see cref="Providers"/> if the profile references a provider.
    /// If the profile has a per-profile CompactionThreshold, it overrides the global one.
    /// </summary>
    public bool SwitchProfile(string name)
    {
        if (Profiles.TryGetValue(name, out var profile))
        {
            // Resolve provider settings
            ProviderConfig? provider = null;
            if (!string.IsNullOrEmpty(profile.Provider) && Providers.TryGetValue(profile.Provider, out var p))
            {
                provider = p;
            }
            LoadProviderApiKeyFromSecureStorage(profile, provider);

            CurrentProfile = name;
            BaseUrl = provider?.BaseUrl ?? profile.BaseUrl ?? BaseUrl;
            if (provider != null && !string.IsNullOrEmpty(provider.ApiKey))
                ApiKey = provider.ApiKey;
            else if (provider == null && !string.IsNullOrEmpty(profile.ApiKey))
                ApiKey = profile.ApiKey;
            Model = profile.Model;
            Temperature = profile.Temperature;
            MaxTokens = profile.MaxTokens;
            ModelTimeoutSeconds = provider?.TimeoutSeconds ?? provider?.ModelTimeoutSeconds ?? profile.ModelTimeoutSeconds ?? ModelTimeoutSeconds;
            ThinkingEnabled = profile.ThinkingEnabled;
            UseSecureApiKeyStorage = provider?.UseSecureApiKeyStorage ?? profile.UseSecureApiKeyStorage;

            // Apply per-profile compaction threshold if set, otherwise keep global
            if (profile.CompactionThreshold.HasValue)
            {
                CompactionThreshold = profile.CompactionThreshold.Value;
                CompactionThresholdConfigured = true;
            }
            return true;
        }
        return false;
    }

    internal static bool NormalizeProfiles(AgentConfig config)
    {
        var changed = false;

        if (config.Profiles.Count == 0 && string.IsNullOrEmpty(config.CurrentProfile))
        {
            config.Providers["default"] = new ProviderConfig
            {
                Name = "default",
                BaseUrl = config.BaseUrl,
                ApiKey = config.ApiKey,
                UseSecureApiKeyStorage = config.UseSecureApiKeyStorage,
                TimeoutSeconds = config.ModelTimeoutSeconds
            };
            config.Profiles["default"] = new ModelProfile
            {
                Name = "default",
                Provider = "default",
                Model = config.Model,
                Temperature = config.Temperature,
                MaxTokens = config.MaxTokens,
                ThinkingEnabled = config.ThinkingEnabled ?? true,
                CompactionThreshold = config.CompactionThresholdConfigured ? config.CompactionThreshold : null
            };
            config.CurrentProfile = "default";
            return true;
        }

        foreach (var (name, profile) in config.Profiles)
        {
            if (string.IsNullOrWhiteSpace(profile.Name))
            {
                profile.Name = name;
                changed = true;
            }

            if (string.IsNullOrWhiteSpace(profile.Provider) && !string.IsNullOrWhiteSpace(profile.BaseUrl))
            {
                var providerName = name;
                config.Providers[providerName] = new ProviderConfig
                {
                    Name = providerName,
                    BaseUrl = profile.BaseUrl.TrimEnd('/'),
                    ApiKey = profile.ApiKey,
                    UseSecureApiKeyStorage = profile.UseSecureApiKeyStorage,
                    TimeoutSeconds = profile.ModelTimeoutSeconds
                };
                profile.Provider = providerName;
                profile.BaseUrl = null;
                profile.ApiKey = null;
                profile.UseSecureApiKeyStorage = false;
                profile.ModelTimeoutSeconds = null;
                changed = true;
            }
        }

        foreach (var provider in config.Providers.Values)
        {
            if (!provider.TimeoutSeconds.HasValue && provider.ModelTimeoutSeconds.HasValue)
            {
                provider.TimeoutSeconds = provider.ModelTimeoutSeconds;
                provider.ModelTimeoutSeconds = null;
                changed = true;
            }
        }

        if (string.IsNullOrEmpty(config.CurrentProfile) && config.Profiles.Count > 0)
        {
            config.CurrentProfile = config.Profiles.ContainsKey("default")
                ? "default"
                : config.Profiles.Keys.First();
            changed = true;
        }

        return changed;
    }

    private static void LoadProviderApiKeyFromSecureStorage(ModelProfile profile, ProviderConfig? provider)
    {
        if (provider == null || !provider.UseSecureApiKeyStorage || !string.IsNullOrEmpty(provider.ApiKey))
            return;

        var credentialStore = SecureCredentialStoreFactory.Create();
        var secureKey = credentialStore.RetrieveAsync($"api-key-{provider.Name}").GetAwaiter().GetResult();
        if (string.IsNullOrEmpty(secureKey) && !string.IsNullOrEmpty(profile.Name))
            secureKey = credentialStore.RetrieveAsync($"api-key-{profile.Name}").GetAwaiter().GetResult();

        if (!string.IsNullOrEmpty(secureKey))
            provider.ApiKey = secureKey;
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
            case "MODEL_TIMEOUT_SECONDS":
            case "AGENT_MODEL_TIMEOUT_SECONDS":
            case "TIMEOUT_SECONDS":
            case "AGENT_TIMEOUT_SECONDS":
                if (int.TryParse(value, out var modelTimeoutSeconds))
                    config.ModelTimeoutSeconds = modelTimeoutSeconds;
                break;
            case "THINKING_ENABLED":
            case "AGENT_THINKING_ENABLED":
                if (bool.TryParse(value, out var thinkingEnabled))
                    config.ThinkingEnabled = thinkingEnabled;
                break;
            case "COMPACT_THRESHOLD":
            case "AGENT_COMPACT_THRESHOLD":
            case "COMPACTION_THRESHOLD":
            case "AGENT_COMPACTION_THRESHOLD":
                if (int.TryParse(value, out var compactThreshold))
                {
                    config.CompactionThreshold = compactThreshold;
                    config.CompactionThresholdConfigured = true;
                }
                break;
            case "TOOLS":
            case "AGENT_TOOLS":
                config.Tools = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                break;
            case "SHELL_ALLOWED_COMMANDS":
            case "AGENT_SHELL_ALLOWED_COMMANDS":
                config.ShellAllowedCommands = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                break;
            case "AUTO_UPDATE_ENABLED":
            case "AGENT_AUTO_UPDATE_ENABLED":
                if (bool.TryParse(value, out var autoUpdateEnabled))
                    config.AutoUpdateEnabled = autoUpdateEnabled;
                break;
            case "AUTO_UPDATE_SOURCE":
            case "AGENT_AUTO_UPDATE_SOURCE":
                config.AutoUpdateSource = value;
                break;
        }
    }

    public void Save()
    {
        var dir = Path.GetDirectoryName(ConfigPath)!;
        if (!Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        // If using secure storage, don't save API key in plaintext for providers
        var providerKeyBackups = new Dictionary<string, string>();
        foreach (var provider in Providers.Values.Where(p => p.UseSecureApiKeyStorage && !string.IsNullOrEmpty(p.ApiKey)))
        {
            var credentialStore = SecureCredentialStoreFactory.Create();
            var success = credentialStore.StoreAsync($"api-key-{provider.Name}", provider.ApiKey!).GetAwaiter().GetResult();
            if (success)
            {
                providerKeyBackups[provider.Name] = provider.ApiKey!;
                provider.ApiKey = null;
            }
        }

        // Handle legacy global secure storage
        var apiKeyBackup = ApiKey;
        if (UseSecureApiKeyStorage && !string.IsNullOrEmpty(ApiKey))
        {
            // Save to secure store and remove from config
            var credentialStore = SecureCredentialStoreFactory.Create();
            var success = credentialStore.StoreAsync("default-api-key", ApiKey).GetAwaiter().GetResult();
            if (success)
            {
                ApiKey = null; // Remove from plaintext config
            }
        }

        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        });

        File.WriteAllText(ConfigPath, json);

        // Restore API keys in memory
        foreach (var (name, key) in providerKeyBackups)
        {
            Providers[name].ApiKey = key;
        }
        if (apiKeyBackup != null)
        {
            ApiKey = apiKeyBackup;
        }
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
