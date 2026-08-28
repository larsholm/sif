namespace sif.agent;

internal sealed record ConversationGoal(
    string Condition,
    string StartedAt,
    int EvaluatedTurns = 0,
    string? LastReason = null,
    string Status = "active",
    string? CompletedAt = null,
    int ConsecutiveTurnsWithoutTools = 0)
{
    internal bool IsActive => Status.Equals("active", StringComparison.OrdinalIgnoreCase);
}
