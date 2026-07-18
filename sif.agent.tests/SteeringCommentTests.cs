using sif.agent.Services;
using Xunit;

namespace sif.agent.tests;

public sealed class SteeringCommentTests
{
    [Theory]
    [InlineData("btw use the existing parser", "use the existing parser")]
    [InlineData("  BTW   keep the API stable  ", "keep the API stable")]
    [InlineData("/btw add a regression test", "add a regression test")]
    public void TryParseQueuesBtwSteeringComments(string input, string expected)
    {
        var parsed = SteeringComment.TryParse(input, out var comment, out var deferUntilToolCall);

        Assert.True(parsed);
        Assert.Equal(expected, comment);
        Assert.True(deferUntilToolCall);
    }

    [Theory]
    [InlineData("use the existing parser")]
    [InlineData("/model use a smaller model")]
    [InlineData("btwNoSpace")]
    public void TryParseTreatsNonBtwInputAsImmediateSteering(string input)
    {
        var parsed = SteeringComment.TryParse(input, out var comment, out var deferUntilToolCall);

        Assert.True(parsed);
        Assert.Equal(input, comment);
        Assert.False(deferUntilToolCall);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    [InlineData(" \t ")]
    [InlineData("btw")]
    [InlineData("btw    ")]
    public void TryParseRejectsEmptyComments(string input)
    {
        var parsed = SteeringComment.TryParse(input, out var comment, out var deferUntilToolCall);

        Assert.False(parsed);
        Assert.Empty(comment);
        Assert.False(deferUntilToolCall);
    }
}
