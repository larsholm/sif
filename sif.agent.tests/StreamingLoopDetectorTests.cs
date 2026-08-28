using sif.agent.Services;
using Xunit;

namespace sif.agent.tests;

public sealed class StreamingLoopDetectorTests
{
    [Fact]
    public void DetectsRepeatedParagraphAcrossArbitraryChunks()
    {
        var detector = new StreamingLoopDetector();
        var paragraph = "I should inspect the same evidence again before deciding what to do next. ";
        var stream = string.Concat(Enumerable.Repeat(paragraph, 8));
        StreamRepetition? detected = null;

        for (var offset = 0; offset < stream.Length; offset += 17)
        {
            detected = detector.Append(stream.Substring(offset, Math.Min(17, stream.Length - offset)));
            if (detected is not null)
                break;
        }

        var repetition = Assert.IsType<StreamRepetition>(detected);
        Assert.True(repetition.Repetitions >= StreamingLoopDetector.MinimumRepetitions);
        Assert.True(repetition.RepeatedCharacters >= StreamingLoopDetector.MinimumRepeatedCharacters);
    }

    [Fact]
    public void IgnoresShortAndNonConsecutiveRepetition()
    {
        var detector = new StreamingLoopDetector();
        var reasoning = string.Join("\n", Enumerable.Range(1, 30).Select(index =>
            $"Step {index}: inspect the evidence, compare it with the request, and choose the next action."));

        var detected = detector.Append(reasoning);

        Assert.Null(detected);
    }

    [Fact]
    public void IgnoresWhitespaceOnlyRepetition()
    {
        var detector = new StreamingLoopDetector();

        var detected = detector.Append(new string(' ', 2000));

        Assert.Null(detected);
    }
}
