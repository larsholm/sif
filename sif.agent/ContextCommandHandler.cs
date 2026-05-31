using Spectre.Console;
using sif.agent.Services;

namespace sif.agent;

/// <summary>
/// Handles the in-chat <c>/context</c> command family: inspecting chat history,
/// stored context entries, and clearing/dropping messages.
/// </summary>
internal static class ContextCommandHandler
{
    public static void Handle(string command, List<ChatMessage> history, string[]? tools, Action showHelp)
    {
        var rest = command.Length == "/context".Length ? "" : command["/context".Length..].Trim();
        if (string.IsNullOrWhiteSpace(rest) || rest.Equals("stats", StringComparison.OrdinalIgnoreCase))
        {
            ShowContextSummary(history, tools);
            return;
        }

        var parts = rest.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var subcommand = parts[0].ToLowerInvariant();
        var arg = parts.Length > 1 ? parts[1].Trim() : "";

        switch (subcommand)
        {
            case "help":
                showHelp();
                break;
            case "list":
                ShowContextEntries();
                break;
            case "messages":
            case "full":
                ShowModelMessages(history, full: true);
                break;
            case "search":
                if (string.IsNullOrWhiteSpace(arg))
                    AnsiConsole.MarkupLine("[yellow]Usage:[/] /context search <query>\n");
                else
                    AnsiConsole.WriteLine(ContextStore.Search(arg));
                break;
            case "read":
                HandleContextRead(arg);
                break;
            case "delete":
            case "remove":
            case "rm":
                HandleContextDelete(arg);
                break;
            case "drop":
                HandleContextDrop(arg, history);
                break;
            case "clear-history":
                ClearChatHistory(history);
                AnsiConsole.MarkupLine("[dim]Conversation cleared.[/]\n");
                break;
            case "clear-store":
                AnsiConsole.MarkupLine($"[dim]Deleted {ContextStore.Clear():N0} stored context entries.[/]\n");
                break;
            case "clear" when string.IsNullOrWhiteSpace(arg) || arg.Equals("history", StringComparison.OrdinalIgnoreCase):
                ClearChatHistory(history);
                AnsiConsole.MarkupLine("[dim]Conversation cleared.[/]\n");
                break;
            case "clear" when arg.Equals("store", StringComparison.OrdinalIgnoreCase):
                AnsiConsole.MarkupLine($"[dim]Deleted {ContextStore.Clear():N0} stored context entries.[/]\n");
                break;
            case "clear" when arg.Equals("all", StringComparison.OrdinalIgnoreCase):
                ClearChatHistory(history);
                AnsiConsole.MarkupLine($"[dim]Conversation cleared. Deleted {ContextStore.Clear():N0} stored context entries.[/]\n");
                break;
            default:
                AnsiConsole.MarkupLine($"[yellow]Unknown /context command:[/] {subcommand.EscapeMarkup()}");
                AnsiConsole.MarkupLine("[dim]Use /context help for available commands.[/]\n");
                break;
        }
    }

    public static string EstimateContextSize(List<ChatMessage> history)
    {
        var chars = history.Sum(m => m.Content.Length);
        var entries = ContextStore.ListEntries();
        var storedChars = entries.Sum(e => e.Length);
        var totalChars = chars + storedChars;
        var tokens = totalChars / 4;
        if (tokens < 1000)
            return $"~{tokens} tokens";
        return $"~{tokens / 1000:0.0}k tokens";
    }

    private static void ShowContextSummary(List<ChatMessage> history, string[]? tools)
    {
        var nonSystemMessages = history.Count(m => m.Role != "system");
        var chars = history.Sum(m => m.Content.Length);
        var entries = ContextStore.ListEntries();
        var storedChars = entries.Sum(e => e.Length);

        var table = new Table();
        table.Title("[green]Current Context[/]");
        table.AddColumn("Area");
        table.AddColumn("Count");
        table.AddColumn("Size");
        table.AddRow("Chat messages", history.Count.ToString("N0"), $"~{chars / 4:N0} tokens / {chars:N0} chars");
        table.AddRow("Non-system messages", nonSystemMessages.ToString("N0"), "");
        table.AddRow("Configured tools", tools is { Length: > 0 } ? string.Join(", ", tools).EscapeMarkup() : "[dim]none[/]", "");
        table.AddRow("Stored context", entries.Count.ToString("N0"), $"{storedChars:N0} chars");
        table.AddRow("Store path", "", $"[dim]{ContextStore.GetRootPath().EscapeMarkup()}[/]");
        AnsiConsole.Write(table);
        ShowModelMessages(history, full: false);
        if (VscodeContext.IsRunningInVscodeTerminal())
            AnsiConsole.MarkupLine("[dim]Note: current VS Code editor context is appended to the next user message when you send it. Use /vscode to inspect it.[/]");
        AnsiConsole.MarkupLine("[dim]Use /context full to show full stored message contents. Tool schemas are also sent when tools are enabled.[/]");
        AnsiConsole.MarkupLine("[dim]Use /context help for management commands.[/]\n");
    }

    private static void ShowModelMessages(List<ChatMessage> history, bool full)
    {
        if (history.Count == 0)
        {
            AnsiConsole.MarkupLine("[dim]No chat messages are currently stored.[/]\n");
            return;
        }

        var table = new Table();
        table.Title(full ? "[green]Stored Model Messages[/]" : "[green]Stored Messages Sent Before Next User Message[/]");
        table.AddColumn("#");
        table.AddColumn("Role");
        table.AddColumn("Chars");
        table.AddColumn(full ? "Content" : "Preview");

        for (var i = 0; i < history.Count; i++)
        {
            var message = history[i];
            var content = full ? message.Content : Preview(message.Content, 220);
            table.AddRow(
                (i + 1).ToString("N0"),
                message.Role.EscapeMarkup(),
                message.Content.Length.ToString("N0"),
                content.EscapeMarkup());
        }

        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();
    }

    private static string Preview(string text, int maxChars)
    {
        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n').Replace('\n', ' ');
        return normalized.Length <= maxChars ? normalized : normalized[..maxChars] + "...";
    }

    private static void ShowContextEntries()
    {
        var entries = ContextStore.ListEntries();
        if (entries.Count == 0)
        {
            AnsiConsole.MarkupLine("[dim]No stored context entries for this session.[/]\n");
            return;
        }

        var table = new Table();
        table.AddColumn("Id");
        table.AddColumn("Source");
        table.AddColumn("Size");
        table.AddColumn("Preview");

        foreach (var entry in entries)
        {
            var preview = entry.Preview.Replace("\r\n", "\n").Replace('\n', ' ');
            if (preview.Length > 90)
                preview = preview[..90] + "...";

            table.AddRow(
                $"[bold]{entry.Id.EscapeMarkup()}[/]",
                entry.Source.EscapeMarkup(),
                $"{entry.Length:N0}",
                preview.EscapeMarkup());
        }

        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();
    }

    private static void HandleContextRead(string arg)
    {
        if (string.IsNullOrWhiteSpace(arg))
        {
            AnsiConsole.MarkupLine("[yellow]Usage:[/] /context read <id> [[query]]\n");
            return;
        }

        var parts = arg.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var id = parts[0];
        var query = parts.Length > 1 ? parts[1] : null;
        AnsiConsole.WriteLine(ContextStore.Read(id, query));
        AnsiConsole.WriteLine();
    }

    private static void HandleContextDelete(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            AnsiConsole.MarkupLine("[yellow]Usage:[/] /context delete <id>\n");
            return;
        }

        var deleted = ContextStore.Delete(id, out var message);
        AnsiConsole.MarkupLine(deleted
            ? $"[dim]{message.EscapeMarkup()}[/]\n"
            : $"[yellow]{message.EscapeMarkup()}[/]\n");
    }

    private static void HandleContextDrop(string arg, List<ChatMessage> history)
    {
        if (!int.TryParse(arg, out var count) || count <= 0)
        {
            AnsiConsole.MarkupLine("[yellow]Usage:[/] /context drop <count>\n");
            return;
        }

        var removed = 0;
        for (var i = history.Count - 1; i >= 0 && removed < count; i--)
        {
            if (history[i].Role == "system")
                continue;

            history.RemoveAt(i);
            removed++;
        }

        AnsiConsole.MarkupLine($"[dim]Removed {removed:N0} recent non-system message(s).[/]\n");
    }

    public static void ClearChatHistory(List<ChatMessage> history)
    {
        var sys = history.FirstOrDefault(m => m.Role == "system");
        history.Clear();
        if (sys != null)
            history.Add(sys);
    }
}
