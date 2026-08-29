using sif.agent;
using sif.agent.Services;
using Xunit;

namespace sif.agent.tests;

public sealed class HistoryCompactorTests
{
    [Fact]
    public void PartitionMessagesRetainsNewestUserBeforeToolHeavyTail()
    {
        var messages = new List<ChatMessage>
        {
            new("user", "older task"),
            new("assistant", "older response"),
            new("user", "active task"),
            new("assistant", "tool result one"),
            new("assistant", "tool result two"),
            new("assistant", "tool result three"),
            new("assistant", "tool result four")
        };

        var (summarized, retained) = HistoryCompactor.PartitionMessagesForCompaction(messages, 4);

        Assert.Equal(["older task", "older response"], summarized.Select(message => message.Content));
        Assert.Equal(
            ["active task", "tool result one", "tool result two", "tool result three", "tool result four"],
            retained.Select(message => message.Content));
        Assert.Contains(retained, message => message.Role == "user");
    }

    [Fact]
    public void PartitionMessagesDoesNotDuplicateUserAlreadyInRecentTail()
    {
        var messages = new List<ChatMessage>
        {
            new("user", "older task"),
            new("assistant", "older response"),
            new("user", "active task"),
            new("assistant", "tool result")
        };

        var (summarized, retained) = HistoryCompactor.PartitionMessagesForCompaction(messages, 4);

        Assert.Empty(summarized);
        Assert.Equal(messages, retained);
    }
}
