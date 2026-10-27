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

        var (summarized, retained) = HistoryCompactor.PartitionMessagesForCompaction(messages, 4, int.MaxValue);

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

        var (summarized, retained) = HistoryCompactor.PartitionMessagesForCompaction(messages, 4, int.MaxValue);

        Assert.Empty(summarized);
        Assert.Equal(messages, retained);
    }

    [Fact]
    public void PartitionMessagesStopsRecentTailAtCharacterBudget()
    {
        var messages = new List<ChatMessage>
        {
            new("user", "active task"),
            new("assistant", new string('a', 600)),
            new("assistant", new string('b', 300)),
            new("assistant", new string('c', 300))
        };

        var (summarized, retained) = HistoryCompactor.PartitionMessagesForCompaction(messages, 4, 700);

        Assert.Equal([new string('a', 600)], summarized.Select(message => message.Content));
        Assert.Equal(["active task", new string('b', 300), new string('c', 300)], retained.Select(message => message.Content));
    }

    [Fact]
    public void PartitionMessagesAlwaysRetainsNewestMessageEvenOverBudget()
    {
        var messages = new List<ChatMessage>
        {
            new("user", "older task"),
            new("assistant", "older response"),
            new("user", "active task"),
            new("assistant", new string('x', 5000))
        };

        var (summarized, retained) = HistoryCompactor.PartitionMessagesForCompaction(messages, 4, 100);

        Assert.Equal(["older task", "older response"], summarized.Select(message => message.Content));
        Assert.Equal(["active task", new string('x', 5000)], retained.Select(message => message.Content));
    }

    [Fact]
    public void AllocateRetainedCharactersKeepsSmallMessagesAndSharesRestAmongLargeOnes()
    {
        var messages = new List<ChatMessage>
        {
            new("user", new string('u', 1000)),
            new("assistant", new string('a', 50000)),
            new("assistant", new string('b', 30000))
        };

        var limits = HistoryCompactor.AllocateRetainedCharacters(messages, 21000);

        Assert.Equal([1000, 10000, 10000], limits);
    }

    [Fact]
    public void AllocateRetainedCharactersKeepsReadableMinimumWhenBudgetIsTiny()
    {
        var messages = new List<ChatMessage>
        {
            new("user", "short"),
            new("assistant", new string('a', 50000))
        };

        var limits = HistoryCompactor.AllocateRetainedCharacters(messages, 100);

        Assert.Equal(5, limits[0]);
        Assert.Equal(2000, limits[1]);
    }

    [Theory]
    [InlineData(27853, 180000, 30, 25065)]
    [InlineData(27853, 180000, 15, 12531)]
    [InlineData(500000, 120000, 30, 108000)]
    [InlineData(null, 0, 30, 162000)]
    public void RetainedCharacterBudgetUsesSmallerOfThresholdAndInputBudget(
        int? availableInputTokens,
        int compactionThreshold,
        int percent,
        int expected)
    {
        Assert.Equal(expected, HistoryCompactor.RetainedCharacterBudget(availableInputTokens, compactionThreshold, 3, percent));
    }
}
