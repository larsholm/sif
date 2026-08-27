using sif.agent;
using Xunit;

namespace sif.agent.tests;

public sealed class ChatResponseParsingThinkingTests
{
    [Fact]
    public void TaggedThinkingIsExtractedAndRemovedFromAnswer()
    {
        const string content = "<think>First thought.</think>\n<thought>Second thought.</thought>Answer.";

        Assert.Equal("First thought.\nSecond thought.", ChatResponseParsing.ExtractThinking(content));
        Assert.Equal("Answer.", ChatResponseParsing.StripThinkingTags(content));
    }
}
