using System.Text.Json.Nodes;
using sif.agent.Services;
using Spectre.Console;
using Xunit;

namespace sif.agent.tests;

public sealed class ConversationTitleTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sif-titles-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void TitleIsSavedAndSurvivesResumeAndCompactionUntilHistoryIsCleared()
    {
        var store = ConversationStore.Create(_root, null);
        store.Save([
            new ChatMessage("system", "System configuration"),
            new ChatMessage("user", "  Fix\t the login\r\npage  "),
            new ChatMessage("assistant", "Investigating")
        ]);

        Assert.Equal("Fix the login page", Assert.Single(ConversationStore.List(_root)).Title);
        Assert.True(ConversationStore.TryOpen(_root, store.Session.Id, out var reopened, out _, out var error), error);
        reopened!.Save([
            new ChatMessage("system", "Compacted conversation summary"),
            new ChatMessage("user", "Now add tests")
        ]);
        Assert.Equal("Fix the login page", Assert.Single(ConversationStore.List(_root)).Title);

        reopened.Save([new ChatMessage("system", "System configuration")]);
        Assert.Null(reopened.Session.Title);
        reopened.Save([new ChatMessage("user", "Build a dashboard")]);
        Assert.Equal("Build a dashboard", Assert.Single(ConversationStore.List(_root)).Title);
    }

    [Fact]
    public void LegacyChatTitleUsesPreviewWithoutReadingHistory()
    {
        var store = ConversationStore.Create(_root, null);
        store.Save([new ChatMessage("user", "Fix the old project")]);
        var metadataPath = Path.Combine(_root, store.Session.Id, "session.json");
        var metadata = JsonNode.Parse(File.ReadAllText(metadataPath))!.AsObject();
        metadata.Remove("Title");
        File.WriteAllText(metadataPath, metadata.ToJsonString());
        File.Delete(Path.Combine(_root, store.Session.Id, "history.json"));

        var session = Assert.Single(ConversationStore.List(_root));
        Assert.Equal("Fix the old project", session.DisplayTitle);
        Assert.Contains("Fix the old project", AgentApp.FormatResumeConfirmation(session));
        Assert.Contains("Fix the old project", AgentApp.FormatResumeChoice(session));
    }

    [Fact]
    public void ResumePromptsEscapeTitlesAndIncludeSessionDetails()
    {
        var store = ConversationStore.Create(_root, null);
        store.Save([new ChatMessage("user", "Fix [red] markup")]);

        var confirmation = AgentApp.FormatResumeConfirmation(store.Session);
        var choice = AgentApp.FormatResumeChoice(store.Session);
        foreach (var markup in new[] { confirmation, choice })
        {
            Assert.Contains("[bold]Fix [[red]] markup[/]", markup);
            Assert.Contains("1 messages", markup);
            Assert.Contains("Fix [red] markup", Markup.Remove(markup));
        }
        Assert.Contains(store.Session.UpdatedAt, confirmation);
        Assert.Contains(store.Session.Id, choice);
    }

    [Theory]
    [InlineData(null, "Untitled chat")]
    [InlineData(" \t\n", "Untitled chat")]
    [InlineData("Short title", "Short title")]
    [InlineData("Please fix the login page and add regression coverage for all browsers", "Please fix the login page and add regression coverage for...")]
    public void TitleIsShortAndReadable(string? message, string expected)
    {
        Assert.Equal(expected, ConversationStore.MakeTitle(message));
    }

    [Fact]
    public void LongWordTitleIsBoundedWithoutSplittingSurrogatePairs()
    {
        var title = ConversationStore.MakeTitle(new string('a', 56) + "😀" + new string('b', 20));
        Assert.Equal(new string('a', 56) + "...", title);
        Assert.True(title.Length <= 60);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
