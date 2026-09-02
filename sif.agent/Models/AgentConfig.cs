using System.Text.Json;
using System.Text.Json.Serialization;
using sif.agent.Services;

namespace sif.agent;

/// <summary>
/// Agent configuration loaded from environment variables, config file, or command-line overrides.
/// </summary>
internal class AgentConfig
{
    public const int DefaultCompactionThreshold = 180000;
    public const string DefaultLoopDetectedMessage = "You were caught in a loop, please continue";

    public static string[] CreateDefaultTools() =>
        ["bash", "read", "edit", "write", "sleep", "serve", "context"];

    public string BaseUrl { get; set; } = "http://localhost:1234/v1";
    public string? ApiKey { get; set; }
    public string Model { get; set; } = "qwen3.6-27b-autoround";
    public int? MaxTokens { get; set; }
    public float? Temperature { get; set; }
    public int? ModelTimeoutSeconds { get; set; }
    public string[]? Tools { get; set; } = CreateDefaultTools();
    public string[]? ShellAllowedCommands { get; set; } = [];
    public bool? ThinkingEnabled { get; set; } = true;
    /// <summary>
    /// Message sent to the model after a repeating reasoning loop is detected.
    /// </summary>
    public string LoopDetectedMessage { get; set; } = DefaultLoopDetectedMessage;
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
    /// The built-in fallback is 180000 tokens. When the provider advertises a
    /// context window and this value was not explicitly configured, runtime setup
    /// replaces it with 60% of that window, capped at the fallback.
    /// Set to 0 to disable compaction.
    /// </summary>
    public int CompactionThreshold { get; set; } = DefaultCompactionThreshold;
    [JsonIgnore]
    public bool CompactionThresholdConfigured { get; set; }
    /// <summary>
    /// Context length reported for the currently loaded model instance. This is
    /// runtime metadata and must never be persisted into the user's config.
    /// </summary>
    [JsonIgnore]
    public int? DetectedContextLength { get; set; }
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
                    config.LoopDetectedMessage = string.IsNullOrWhiteSpace(loaded.LoopDetectedMessage)
                        ? DefaultLoopDetectedMessage
                        : loaded.LoopDetectedMessage;
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

                    if (MigrateLegacyFormat(config, json))
                        config.Save();
                }
            }
            catch
            {
                // Ignore parse errors
            }
        }

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
        if (Environment.GetEnvironmentVariable("AGENT_LOOP_DETECTED_MESSAGE") is { Length: > 0 } envLoopDetectedMessage)
            config.LoopDetectedMessage = envLoopDetectedMessage;
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
            // Authentication belongs to the selected provider/profile. Assign null as
            // well so a keyless local provider cannot inherit the previous provider's key.
            ApiKey = provider?.ApiKey ?? profile.ApiKey;
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
            if (!string.Equals(profile.Name, name, StringComparison.Ordinal))
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

        foreach (var (name, provider) in config.Providers)
        {
            if (!string.Equals(provider.Name, name, StringComparison.Ordinal))
            {
                provider.Name = name;
                changed = true;
            }

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

    internal static bool MigrateLegacyFormat(AgentConfig config, string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var hadProfiles = root.TryGetProperty(nameof(Profiles), out var profilesElement) &&
                          profilesElement.ValueKind == JsonValueKind.Object &&
                          profilesElement.EnumerateObject().Any();
        var hadValues = config.Values.Count > 0;
        var hadFlatSettings = LegacyFlatSettingNames.Any(name => root.TryGetProperty(name, out _));

        // Values was an intermediate persistence format. Materialize its recognized
        // settings before converting the flat active-model snapshot.
        ApplyValues(config);

        var changed = NormalizeProfiles(config);
        if (hadProfiles && (hadFlatSettings || hadValues))
            changed |= MigrateActiveFlatSettings(config, root);

        if (hadValues)
        {
            config.Values.Clear();
            changed = true;
        }

        // Even when all values already agree, rewrite files containing legacy root
        // fields so the next save has only the provider/profile representation.
        return changed || hadFlatSettings;
    }

    private static readonly string[] LegacyFlatSettingNames =
    [
        nameof(BaseUrl),
        nameof(ApiKey),
        nameof(Model),
        nameof(MaxTokens),
        nameof(Temperature),
        nameof(ModelTimeoutSeconds),
        nameof(ThinkingEnabled),
        nameof(UseSecureApiKeyStorage)
    ];

    private static bool MigrateActiveFlatSettings(AgentConfig config, JsonElement root)
    {
        if (string.IsNullOrWhiteSpace(config.CurrentProfile) ||
            !config.Profiles.TryGetValue(config.CurrentProfile, out var profile))
        {
            return false;
        }

        var changed = false;
        var providerName = string.IsNullOrWhiteSpace(profile.Provider)
            ? config.CurrentProfile
            : profile.Provider;

        if (!config.Providers.TryGetValue(providerName, out var provider))
        {
            provider = new ProviderConfig { Name = providerName };
            config.Providers[providerName] = provider;
            changed = true;
        }

        if (!string.Equals(profile.Provider, providerName, StringComparison.Ordinal))
        {
            profile.Provider = providerName;
            changed = true;
        }

        if (HasLegacySetting(root, config, nameof(BaseUrl)) && provider.BaseUrl != config.BaseUrl)
        {
            provider.BaseUrl = config.BaseUrl.TrimEnd('/');
            changed = true;
        }
        if (HasLegacySetting(root, config, nameof(ApiKey)) && provider.ApiKey != config.ApiKey)
        {
            provider.ApiKey = config.ApiKey;
            changed = true;
        }
        if (HasLegacySetting(root, config, nameof(UseSecureApiKeyStorage)) &&
            provider.UseSecureApiKeyStorage != config.UseSecureApiKeyStorage)
        {
            provider.UseSecureApiKeyStorage = config.UseSecureApiKeyStorage;
            changed = true;
        }
        if (HasLegacySetting(root, config, nameof(ModelTimeoutSeconds), "TimeoutSeconds") &&
            provider.TimeoutSeconds != config.ModelTimeoutSeconds)
        {
            provider.TimeoutSeconds = config.ModelTimeoutSeconds;
            changed = true;
        }
        if (HasLegacySetting(root, config, nameof(Model)) && profile.Model != config.Model)
        {
            profile.Model = config.Model;
            changed = true;
        }
        if (HasLegacySetting(root, config, nameof(Temperature)) && profile.Temperature != config.Temperature)
        {
            profile.Temperature = config.Temperature;
            changed = true;
        }
        if (HasLegacySetting(root, config, nameof(MaxTokens)) && profile.MaxTokens != config.MaxTokens)
        {
            profile.MaxTokens = config.MaxTokens;
            changed = true;
        }
        if (HasLegacySetting(root, config, nameof(ThinkingEnabled)) &&
            profile.ThinkingEnabled != (config.ThinkingEnabled ?? true))
        {
            profile.ThinkingEnabled = config.ThinkingEnabled ?? true;
            changed = true;
        }

        return changed;
    }

    private static bool HasLegacySetting(
        JsonElement root,
        AgentConfig config,
        string propertyName,
        params string[] aliases)
    {
        if (root.TryGetProperty(propertyName, out _))
            return true;

        var normalizedNames = aliases
            .Append(propertyName)
            .Select(NormalizeSettingName)
            .ToHashSet(StringComparer.Ordinal);
        return config.Values.Keys.Any(key =>
            normalizedNames.Contains(NormalizeSettingName(key)));
    }

    private static string NormalizeSettingName(string name)
    {
        return name.Replace("AGENT_", "", StringComparison.OrdinalIgnoreCase)
            .Replace("_", "", StringComparison.Ordinal)
            .ToUpperInvariant();
    }

    private static void LoadProviderApiKeyFromSecureStorage(ModelProfile profile, ProviderConfig? provider)
    {
        if (provider == null || !provider.UseSecureApiKeyStorage || !string.IsNullOrEmpty(provider.ApiKey))
            return;

        var credentialStore = SecureCredentialStoreFactory.Create();
        var secureKey = credentialStore.RetrieveAsync($"api-key-{provider.Name}").GetAwaiter().GetResult();
        if (string.IsNullOrEmpty(secureKey) && !string.IsNullOrEmpty(profile.Name))
            secureKey = credentialStore.RetrieveAsync($"api-key-{profile.Name}").GetAwaiter().GetResult();
        if (string.IsNullOrEmpty(secureKey) && string.Equals(provider.Name, "default", StringComparison.Ordinal))
            secureKey = credentialStore.RetrieveAsync("default-api-key").GetAwaiter().GetResult();

        if (!string.IsNullOrEmpty(secureKey))
            provider.ApiKey = secureKey;
    }

    public void ApplyValue(string key, string value)
    {
        ApplyValue(this, key, value);
        UpdateActiveProfileSetting(key);
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
            case "LOOP_DETECTED_MESSAGE":
            case "AGENT_LOOP_DETECTED_MESSAGE":
                config.LoopDetectedMessage = string.IsNullOrWhiteSpace(value)
                    ? DefaultLoopDetectedMessage
                    : value;
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

    private void UpdateActiveProfileSetting(string key)
    {
        if (string.IsNullOrWhiteSpace(CurrentProfile) || !Profiles.TryGetValue(CurrentProfile, out var profile))
            return;

        var normalizedKey = NormalizeSettingName(key);
        ProviderConfig? provider = null;
        if (normalizedKey is "BASEURL" or "APIKEY" or "MODELTIMEOUTSECONDS" or "TIMEOUTSECONDS")
        {
            var providerName = string.IsNullOrWhiteSpace(profile.Provider) ? CurrentProfile : profile.Provider;
            if (!Providers.TryGetValue(providerName, out provider))
            {
                provider = new ProviderConfig { Name = providerName };
                Providers[providerName] = provider;
            }
            profile.Provider = providerName;
        }

        switch (normalizedKey)
        {
            case "BASEURL" when provider != null:
                provider.BaseUrl = BaseUrl;
                break;
            case "APIKEY" when provider != null:
                provider.ApiKey = ApiKey;
                break;
            case "MODEL":
                profile.Model = Model;
                break;
            case "MAXTOKENS":
                profile.MaxTokens = MaxTokens;
                break;
            case "TEMPERATURE":
                profile.Temperature = Temperature;
                break;
            case "MODELTIMEOUTSECONDS" when provider != null:
            case "TIMEOUTSECONDS" when provider != null:
                provider.TimeoutSeconds = ModelTimeoutSeconds;
                break;
            case "THINKINGENABLED":
                profile.ThinkingEnabled = ThinkingEnabled ?? true;
                break;
        }
    }

    internal void UpdateActiveProfileFromFlat()
    {
        if (string.IsNullOrWhiteSpace(CurrentProfile) || !Profiles.TryGetValue(CurrentProfile, out var profile))
            return;

        var providerName = string.IsNullOrWhiteSpace(profile.Provider) ? CurrentProfile : profile.Provider;
        if (!Providers.TryGetValue(providerName, out var provider))
        {
            provider = new ProviderConfig { Name = providerName };
            Providers[providerName] = provider;
        }

        profile.Provider = providerName;
        provider.BaseUrl = BaseUrl.TrimEnd('/');
        provider.ApiKey = ApiKey;
        provider.UseSecureApiKeyStorage = UseSecureApiKeyStorage;
        provider.TimeoutSeconds = ModelTimeoutSeconds;
        profile.Model = Model;
        profile.Temperature = Temperature;
        profile.MaxTokens = MaxTokens;
        profile.ThinkingEnabled = ThinkingEnabled ?? true;
    }

    public void Save()
    {
        NormalizeProfiles(this);

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

        var json = ToFileJson();

        File.WriteAllText(ConfigPath, json);

        // Restore API keys in memory
        foreach (var (name, key) in providerKeyBackups)
        {
            Providers[name].ApiKey = key;
        }
    }

    internal string ToFileJson()
    {
        var persisted = new
        {
            Tools,
            ShellAllowedCommands,
            AutoUpdateEnabled,
            AutoUpdateSource,
            LoopDetectedMessage,
            Providers,
            Profiles,
            CurrentProfile,
            CompactionThreshold,
            McpServers
        };

        return JsonSerializer.Serialize(persisted, new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never
        });
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
