namespace sif.agent.Services;

/// <summary>
/// Parses a short, in-flight instruction entered while an agent turn is running.
/// </summary>
internal static class SteeringComment
{
    /// <summary>
    /// Parses <c>btw &lt;comment&gt;</c> and <c>/btw &lt;comment&gt;</c> as deferred
    /// comments. Every other non-empty input is an immediate comment.
    /// </summary>
    public static bool TryParse(string input, out string comment, out bool deferUntilToolCall)
    {
        var trimmed = input.Trim();
        if (trimmed.Length == 0)
        {
            comment = string.Empty;
            deferUntilToolCall = false;
            return false;
        }

        var command = trimmed.StartsWith('/') ? trimmed[1..].TrimStart() : trimmed;

        if (command.StartsWith("btw", StringComparison.OrdinalIgnoreCase) &&
            (command.Length == 3 || char.IsWhiteSpace(command[3])))
        {
            comment = command.Length == 3 ? string.Empty : command[3..].Trim();
            deferUntilToolCall = comment.Length > 0;
            return comment.Length > 0;
        }

        comment = trimmed;
        deferUntilToolCall = false;
        return true;
    }
}
