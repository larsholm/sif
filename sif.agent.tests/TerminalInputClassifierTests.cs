using sif.agent.Services;
using Xunit;

namespace sif.agent.tests;

public sealed class TerminalInputClassifierTests
{
    [Fact]
    public void EnterWithQueuedInputIsPasteNewline()
    {
        var enter = new ConsoleKeyInfo('\r', ConsoleKey.Enter, false, false, false);

        Assert.True(TerminalInputClassifier.IsQueuedPasteNewline(enter, hasQueuedInput: true));
    }

    [Fact]
    public void EnterWithoutQueuedInputSubmitsPrompt()
    {
        var enter = new ConsoleKeyInfo('\r', ConsoleKey.Enter, false, false, false);

        Assert.False(TerminalInputClassifier.IsQueuedPasteNewline(enter, hasQueuedInput: false));
    }

    [Fact]
    public void QueuedPrintableKeyIsNotPasteNewline()
    {
        var letter = new ConsoleKeyInfo('a', ConsoleKey.A, false, false, false);

        Assert.False(TerminalInputClassifier.IsQueuedPasteNewline(letter, hasQueuedInput: true));
    }
}
