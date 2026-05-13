using System.Text.Json.Serialization;

namespace sif.agent;

/// <summary>
/// A named model profile that encapsulates all connection and model settings.
/// This allows users to define multiple local or cloud models and switch between them easily.
/// </summary>
internal class ModelProfile
{
    /// <summary>Human-readable name for this profile (e.g., "local-qwen", "openai-o3", "my-ollama").</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Base URL of the OpenAI-compatible API endpoint (e.g., "http://localhost:1234/v1" or "https://api.openai.com/v1").</summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>API key. Leave empty for local models that don't require authentication.</summary>
    public string? ApiKey { get; set; }

    /// <summary>Model identifier to use with this endpoint (e.g., "qwen3.6-27b-autoround", "gpt-4o", "llama3.1").</summary>
    public string Model { get; set; } = string.Empty;

    /// <summary>Sampling temperature (0.0 to 2.0). Null means server default.</summary>
    public float? Temperature { get; set; }

    /// <summary>Maximum tokens to generate. Null means server default.</summary>
    public int? MaxTokens { get; set; }

    /// <summary>Enable model thinking/reasoning when the backend exposes it.</summary>
    public bool ThinkingEnabled { get; set; } = true;

    /// <summary>
    /// Per-profile token threshold for compaction. Null means use the global default (60000).
    /// Different models with different context windows may benefit from different thresholds.
    /// </summary>
    public int? CompactionThreshold { get; set; }

    /// <summary>
    /// Short one-line description for display in lists (auto-generated from Name, Model, and BaseUrl).
    /// Not persisted to the config file — populated at read time.
    /// </summary>
    [JsonIgnore]
    public string DisplayLabel => string.IsNullOrEmpty(Model) ? Name : $"{Name} ({Model} @ {BaseUrl})";

    /// <summary>
    /// Build a merged AgentConfig from this profile, optionally overridden by CLI args and environment.
    /// </summary>
    public AgentConfig ToConfig(string? overrideBaseUrl = null, string? overrideApiKey = null, string? overrideModel = null, float? overrideTemperature = null, int? overrideMaxTokens = null)
    {
        var config = new AgentConfig();
        config.BaseUrl = overrideBaseUrl ?? BaseUrl;
        config.ApiKey = overrideApiKey ?? ApiKey;
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
