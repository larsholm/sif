using sif.agent.Services;
using Xunit;

namespace sif.agent.tests;

public sealed class BracketedPasteBufferTests
{
    private const string EndMarker = "\u001b[201~";

    [Fact]
    public void AppendDetectsEndMarkerAndNormalizesNewlines()
    {
        var buffer = new BracketedPasteBuffer();
        var completed = false;

        foreach (var character in "first\r\nsecond\rthird\n" + EndMarker)
            completed = buffer.Append(character);

        Assert.True(completed);
        Assert.Equal("first\nsecond\nthird\n", buffer.GetText());
    }

    [Fact]
    public void AppendHandlesLargeMultilinePaste()
    {
        var buffer = new BracketedPasteBuffer();
        var pasted = string.Join('\n', Enumerable.Range(0, 20_000).Select(index => $"line {index}: some pasted text"));
        var completed = false;

        foreach (var character in pasted + EndMarker)
            completed = buffer.Append(character);

        Assert.True(completed);
        Assert.Equal(pasted, buffer.GetText());
    }

    [Fact]
    public void AppendDoesNotCompleteForPartialEndMarker()
    {
        var buffer = new BracketedPasteBuffer();
        var completed = false;

        foreach (var character in "text\u001b[201")
            completed = buffer.Append(character);

        Assert.False(completed);
        Assert.Equal("text\u001b[201", buffer.GetText());
    }
}
