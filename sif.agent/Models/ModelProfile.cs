using System.Text.Json.Serialization;

namespace sif.agent;

/// <summary>
/// A named model configuration that specifies which model to use and model-specific settings.
/// Decoupled from provider selection — the provider (endpoint + auth) is referenced by name.
/// </summary>
internal class ModelProfile
{
    /// <summary>Human-readable name for this model profile (e.g., "qwen3.6", "gpt-4o", "llama3.1").</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Name of the provider config to use for this model.</summary>
    public string? Provider { get; set; }

    /// <summary>Legacy profile endpoint, read from older configs and migrated to a provider.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? BaseUrl { get; set; }

    /// <summary>Legacy profile API key, read from older configs and migrated to a provider.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ApiKey { get; set; }

    /// <summary>Legacy profile secure-storage flag, read from older configs and migrated to a provider.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool UseSecureApiKeyStorage { get; set; }

    /// <summary>Legacy profile timeout, read from older configs and migrated to a provider.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ModelTimeoutSeconds { get; set; }

    /// <summary>Model identifier to use with the provider endpoint (e.g., "qwen3.6-27b-autoround", "gpt-4o", "llama3.1").</summary>
    public string Model { get; set; } = string.Empty;

    /// <summary>Sampling temperature (0.0 to 2.0). Null means server default.</summary>
    public float? Temperature { get; set; }

    /// <summary>Maximum tokens to generate. Null means server default.</summary>
    public int? MaxTokens { get; set; }

    /// <summary>Enable model thinking/reasoning when the backend exposes it.</summary>
    public bool ThinkingEnabled { get; set; } = true;

    /// <summary>
    /// Per-profile token threshold for compaction. Null means use the global default (100000).
    /// Different models with different context windows may benefit from different thresholds.
    /// </summary>
    public int? CompactionThreshold { get; set; }

    /// <summary>
    /// Short display label (auto-generated from Name, Model, and Provider).
    /// Not persisted to the config file — populated at read time.
    /// </summary>
    [JsonIgnore]
    public string DisplayLabel
    {
        get
        {
            var parts = new List<string> { Name };
            if (!string.IsNullOrEmpty(Model) && Model != Name)
                parts.Add(Model);
            if (!string.IsNullOrEmpty(Provider))
                parts.Add($"@{Provider}");
            return string.Join(" ", parts);
        }
    }

    /// <summary>
    /// Build a merged AgentConfig from this profile, resolving the provider and
    /// optionally overridden by CLI args and environment.
    /// </summary>
    public AgentConfig ToConfig(
        Dictionary<string, ProviderConfig>? providers = null,
        string? overrideBaseUrl = null,
        string? overrideApiKey = null,
        string? overrideModel = null,
        float? overrideTemperature = null,
        int? overrideMaxTokens = null,
        int? overrideTimeoutSeconds = null)
    {
        var config = new AgentConfig();

        // Resolve provider settings
        ProviderConfig? provider = null;
        if (!string.IsNullOrEmpty(Provider) && providers != null && providers.TryGetValue(Provider, out var p))
        {
            provider = p;
        }

        config.BaseUrl = overrideBaseUrl ?? provider?.BaseUrl ?? BaseUrl ?? string.Empty;
        config.ApiKey = overrideApiKey ?? provider?.ApiKey ?? ApiKey;
        config.ModelTimeoutSeconds = overrideTimeoutSeconds ?? provider?.TimeoutSeconds ?? provider?.ModelTimeoutSeconds ?? ModelTimeoutSeconds;
        config.UseSecureApiKeyStorage = provider?.UseSecureApiKeyStorage ?? UseSecureApiKeyStorage;

        config.Model = overrideModel ?? Model;
        config.Temperature = overrideTemperature ?? Temperature;
        config.MaxTokens = overrideMaxTokens ?? MaxTokens;
        config.ThinkingEnabled = ThinkingEnabled;

        // Apply per-profile compaction threshold if set
        if (CompactionThreshold.HasValue)
        {
            config.CompactionThreshold = CompactionThreshold.Value;
            config.CompactionThresholdConfigured = true;
        }

        return config;
    }
}
