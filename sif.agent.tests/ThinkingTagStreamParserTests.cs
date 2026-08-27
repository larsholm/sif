using sif.agent.Services;
using Xunit;

namespace sif.agent.tests;

public sealed class ThinkingTagStreamParserTests
{
    [Fact]
    public void SeparatesThinkingTagsSplitAcrossChunks()
    {
        var parser = new ThinkingTagStreamParser();
        var segments = new List<StreamTextSegment>();

        segments.AddRange(parser.Append("<thi"));
        segments.AddRange(parser.Append("nk>Inspect the paste.</th"));
        segments.AddRange(parser.Append("ink>Final answer"));
        segments.AddRange(parser.Complete());

        Assert.Collection(
            segments,
            segment =>
            {
                Assert.True(segment.IsReasoning);
                Assert.Equal("Inspect the paste.", segment.Text);
            },
            segment =>
            {
                Assert.False(segment.IsReasoning);
                Assert.Equal("Final answer", segment.Text);
            });
    }

    [Fact]
    public void PreservesPartialTagLikeTextWhenStreamCompletes()
    {
        var parser = new ThinkingTagStreamParser();
        var segments = new List<StreamTextSegment>();

        segments.AddRange(parser.Append("Use <thin"));
        segments.AddRange(parser.Complete());

        Assert.Equal("Use <thin", string.Concat(segments.Select(segment => segment.Text)));
        Assert.All(segments, segment => Assert.False(segment.IsReasoning));
    }
}
