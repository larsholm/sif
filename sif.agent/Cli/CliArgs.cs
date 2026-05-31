namespace sif.agent;

/// <summary>
/// Parsed command-line options shared across agent commands.
/// </summary>
internal class CliArgs
{
    public List<string>? Positional { get; set; }
    public string? SystemPrompt { get; set; }
    public string? Model { get; set; }
    public string? Profile { get; set; }
    public string? BaseUrl { get; set; }
    public string? ApiKey { get; set; }
    public bool NoStream { get; set; }
    public string[]? Tools { get; set; }
    public bool? Thinking { get; set; }
    public float? Temperature { get; set; }
    public int? MaxTokens { get; set; }
}
