using sif.agent;
using sif.agent.Services;
using Xunit;

namespace sif.agent.tests;

public sealed class GoalEvaluatorTests
{
    [Theory]
    [InlineData("met", 1)]
    [InlineData("not_met", 0)]
    [InlineData("not met", 0)]
    [InlineData("impossible", 2)]
    public void ParsesStructuredEvaluatorVerdicts(string verdict, int expected)
    {
        var result = GoalEvaluator.Parse($$"""
            ```json
            {"verdict":"{{verdict}}","reason":"Concrete evaluator reason."}
            ```
            """);

        Assert.Equal((GoalVerdict)expected, result.Verdict);
        Assert.Equal("Concrete evaluator reason.", result.Reason);
    }

    [Fact]
    public void InvalidEvaluatorResponseContinuesCautiously()
    {
        var result = GoalEvaluator.Parse("probably finished");

        Assert.Equal(GoalVerdict.NotMet, result.Verdict);
        Assert.Contains("invalid verdict", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EvidenceExcludesSystemPromptAndKeepsRecentConversation()
    {
        var history = new List<ChatMessage>
        {
            new("system", "secret system instructions"),
            new("user", "Run the focused tests."),
            new("assistant", "Tool call from prior turn: bash\nResult:\nPassed: 12"),
            new("assistant", "All focused tests pass.")
        };

        var evidence = GoalEvaluator.BuildEvidence(history);

        Assert.DoesNotContain("secret system instructions", evidence);
        Assert.Contains("Run the focused tests.", evidence);
        Assert.Contains("Passed: 12", evidence);
        Assert.Contains("All focused tests pass.", evidence);
    }

    [Fact]
    public void GoalLoopContinuesUnmetGoalAndResetsNoProgressAfterToolUse()
    {
        var goal = new ConversationGoal(
            "Tests pass.",
            DateTimeOffset.UtcNow.ToString("O"),
            ConsecutiveTurnsWithoutTools: 2);

        var decision = GoalLoop.Apply(
            goal,
            new GoalEvaluation(GoalVerdict.NotMet, "One test still fails."),
            usedTools: true,
            DateTimeOffset.UtcNow);

        Assert.True(decision.ContinueAutomatically);
        Assert.False(decision.PausedForNoProgress);
        Assert.True(decision.Goal.IsActive);
        Assert.Equal(1, decision.Goal.EvaluatedTurns);
        Assert.Equal(0, decision.Goal.ConsecutiveTurnsWithoutTools);
        Assert.Equal("One test still fails.", decision.Goal.LastReason);
    }

    [Fact]
    public void GoalLoopPausesAfterThreeTurnsWithoutTools()
    {
        var goal = new ConversationGoal(
            "Tests pass.",
            DateTimeOffset.UtcNow.ToString("O"),
            ConsecutiveTurnsWithoutTools: 2);

        var decision = GoalLoop.Apply(
            goal,
            new GoalEvaluation(GoalVerdict.NotMet, "No new evidence."),
            usedTools: false,
            DateTimeOffset.UtcNow);

        Assert.False(decision.ContinueAutomatically);
        Assert.True(decision.PausedForNoProgress);
        Assert.True(decision.Goal.IsActive);
        Assert.Equal(3, decision.Goal.ConsecutiveTurnsWithoutTools);
    }

    [Theory]
    [InlineData(1, "achieved")]
    [InlineData(2, "impossible")]
    public void GoalLoopEndsOnTerminalVerdict(int verdict, string expectedStatus)
    {
        var goal = new ConversationGoal("Tests pass.", DateTimeOffset.UtcNow.ToString("O"));

        var decision = GoalLoop.Apply(
            goal,
            new GoalEvaluation((GoalVerdict)verdict, "Terminal reason."),
            usedTools: true,
            DateTimeOffset.UtcNow);

        Assert.False(decision.ContinueAutomatically);
        Assert.False(decision.Goal.IsActive);
        Assert.Equal(expectedStatus, decision.Goal.Status);
        Assert.NotNull(decision.Goal.CompletedAt);
    }
}
