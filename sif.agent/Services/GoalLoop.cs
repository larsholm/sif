namespace sif.agent.Services;

internal sealed record GoalLoopDecision(
    ConversationGoal Goal,
    bool ContinueAutomatically,
    bool PausedForNoProgress);

internal static class GoalLoop
{
    internal const int MaximumTurnsWithoutTools = 3;

    internal static GoalLoopDecision Apply(
        ConversationGoal goal,
        GoalEvaluation evaluation,
        bool usedTools,
        DateTimeOffset evaluatedAt)
    {
        var consecutiveTurnsWithoutTools = usedTools
            ? 0
            : goal.ConsecutiveTurnsWithoutTools + 1;
        var updated = goal with
        {
            EvaluatedTurns = goal.EvaluatedTurns + 1,
            LastReason = evaluation.Reason,
            ConsecutiveTurnsWithoutTools = consecutiveTurnsWithoutTools
        };

        if (evaluation.Verdict == GoalVerdict.Met)
        {
            updated = updated with
            {
                Status = "achieved",
                CompletedAt = evaluatedAt.ToString("O")
            };
            return new GoalLoopDecision(updated, false, false);
        }

        if (evaluation.Verdict == GoalVerdict.Impossible)
        {
            updated = updated with
            {
                Status = "impossible",
                CompletedAt = evaluatedAt.ToString("O")
            };
            return new GoalLoopDecision(updated, false, false);
        }

        var paused = consecutiveTurnsWithoutTools >= MaximumTurnsWithoutTools;
        return new GoalLoopDecision(updated, !paused, paused);
    }
}
