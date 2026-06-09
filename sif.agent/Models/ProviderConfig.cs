using System.Text.Json.Serialization;

namespace sif.agent;

/// <summary>
/// Configuration for a model provider (API endpoint).
/// Decoupled from model selection so the same provider can be reused across multiple models.
/// </summary>
internal class ProviderConfig
{
    /// <summary>Human-readable name for this provider (e.g., "local", "openai", "anthropic").</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Base URL of the OpenAI-compatible API endpoint.</summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>API key. Leave empty for local models that don't require authentication.</summary>
    public string? ApiKey { get; set; }

    /// <summary>If true, the API key for this provider is stored in the OS secure credential store.</summary>
    public bool UseSecureApiKeyStorage { get; set; }

    /// <summary>Network timeout for requests to this provider in seconds. Null means SDK default.</summary>
    public int? TimeoutSeconds { get; set; }

    /// <summary>
    /// Short display label (auto-generated from Name and BaseUrl).
    /// Not persisted to the config file.
    /// </summary>
    [JsonIgnore]
    public string DisplayLabel => $"{Name} ({BaseUrl})";
}
