using sif.agent.Services;
using Xunit;

namespace sif.agent.tests;

public sealed class SteeringCommentTests
{
    [Theory]
    [InlineData("btw use the existing parser", "use the existing parser")]
    [InlineData("  BTW   keep the API stable  ", "keep the API stable")]
    [InlineData("/btw add a regression test", "add a regression test")]
    public void TryParseAcceptsSteeringComments(string input, string expected)
    {
        var parsed = SteeringComment.TryParse(input, out var comment);

        Assert.True(parsed);
        Assert.Equal(expected, comment);
    }

    [Theory]
    [InlineData("")]
    [InlineData("btw")]
    [InlineData("btw    ")]
    [InlineData("by the way, use the parser")]
    [InlineData("btwNoSpace")]
    public void TryParseRejectsNonCommandsOrEmptyComments(string input)
    {
        var parsed = SteeringComment.TryParse(input, out var comment);

        Assert.False(parsed);
        Assert.Empty(comment);
    }
}
