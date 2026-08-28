namespace sif.agent.Services;

internal static class TerminalInputClassifier
{
    internal static bool IsQueuedPasteNewline(ConsoleKeyInfo key, bool hasQueuedInput) =>
        key.Key == ConsoleKey.Enter && hasQueuedInput;
}
