namespace sif.agent.Services;

/// <summary>
/// Parses a short, in-flight instruction entered while an agent turn is running.
/// </summary>
internal static class SteeringComment
{
    /// <summary>
    /// Accepts <c>btw &lt;comment&gt;</c> and <c>/btw &lt;comment&gt;</c>, ignoring case.
    /// </summary>
    public static bool TryParse(string input, out string comment)
    {
        var trimmed = input.Trim();
        if (trimmed.StartsWith('/'))
            trimmed = trimmed[1..].TrimStart();

        if (!trimmed.StartsWith("btw", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Length == 3 ||
            !char.IsWhiteSpace(trimmed[3]))
        {
            comment = string.Empty;
            return false;
        }

        comment = trimmed[3..].Trim();
        return comment.Length > 0;
    }
}
