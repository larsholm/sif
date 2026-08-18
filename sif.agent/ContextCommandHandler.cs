using Spectre.Console;
using sif.agent.Services;

namespace sif.agent;

/// <summary>
/// Handles the in-chat <c>/context</c> command family: inspecting chat history,
/// stored context entries, and clearing/dropping messages.
/// </summary>
internal static class ContextCommandHandler
{
    public static void Handle(string command, List<ChatMessage> history, AgentClient client, Action showHelp)
    {
        var rest = command.Length == "/context".Length ? "" : command["/context".Length..].Trim();
        if (string.IsNullOrWhiteSpace(rest) || rest.Equals("stats", StringComparison.OrdinalIgnoreCase))
        {
            ShowContextSummary(history, client.LastRequestSnapshot);
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
                ShowLastRequest(client.LastRequestSnapshot, full: true);
                break;
            case "history":
            case "stored":
                ShowMessages(history, "Stored Conversation History", full: true);
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

    public static string EstimateContextSize(ModelRequestSnapshot? snapshot)
    {
        if (snapshot is null)
            return "unknown";

        var tokens = snapshot.ApproximateInputCharacters / 4;
        if (tokens < 1000)
            return $"~{tokens} tokens";
        return $"~{tokens / 1000:0.0}k tokens";
    }

    private static void ShowContextSummary(List<ChatMessage> history, ModelRequestSnapshot? snapshot)
    {
        ShowLastRequest(snapshot, full: false);

        var nonSystemMessages = history.Count(m => m.Role != "system");
        var chars = history.Sum(m => m.Content.Length);
        var entries = ContextStore.ListEntries();
        var storedChars = entries.Sum(e => e.Length);

        var table = new Table();
        table.Title("[green]Persisted State[/]");
        table.AddColumn("Area");
        table.AddColumn("Count");
        table.AddColumn("Size");
        table.AddRow("Conversation history", history.Count.ToString("N0"), $"~{chars / 4:N0} tokens / {chars:N0} chars");
        table.AddRow("Non-system messages", nonSystemMessages.ToString("N0"), "");
        table.AddRow("Out-of-band context", entries.Count.ToString("N0"), $"{storedChars:N0} chars");
        table.AddRow("Store path", "", $"[dim]{ContextStore.GetRootPath().EscapeMarkup()}[/]");
        AnsiConsole.Write(table);
        if (VscodeContext.IsRunningInVscodeTerminal())
            AnsiConsole.MarkupLine("[dim]Note: current VS Code editor context is appended to the next user message when you send it. Use /vscode to inspect it.[/]");
        AnsiConsole.MarkupLine("[dim]Use /context full for the complete last request, including tool schemas; /context history shows persisted conversation state.[/]");
        AnsiConsole.MarkupLine("[dim]Use /context help for management commands.[/]\n");
    }

    private static void ShowLastRequest(ModelRequestSnapshot? snapshot, bool full)
    {
        if (snapshot is null)
        {
            AnsiConsole.MarkupLine("[dim]No main chat request has been captured for the current conversation.[/]\n");
            return;
        }

        var metadata = new Table();
        metadata.Title("[green]Last Model Request[/]");
        metadata.AddColumn("Item");
        metadata.AddColumn("Value");
        metadata.AddRow("Captured", snapshot.CapturedAt.ToString("u").EscapeMarkup());
        metadata.AddRow("Model", snapshot.Model.EscapeMarkup());
        metadata.AddRow("Mode", snapshot.Streaming ? "streaming" : "non-streaming");
        metadata.AddRow("Messages", snapshot.Messages.Count.ToString("N0"));
        metadata.AddRow("Approx. input", $"~{snapshot.ApproximateInputCharacters / 4:N0} tokens / {snapshot.ApproximateInputCharacters:N0} chars");
        metadata.AddRow("Tools", snapshot.Tools.Count == 0
            ? "[dim]none[/]"
            : string.Join(", ", snapshot.Tools.Select(tool => tool.Name)).EscapeMarkup());
        metadata.AddRow("Temperature", snapshot.Temperature?.ToString() ?? "[dim]provider default[/]");
        metadata.AddRow("Max output tokens", snapshot.MaxOutputTokens?.ToString("N0") ?? "[dim]provider default[/]");
        metadata.AddRow("Reasoning effort", snapshot.ReasoningEffort?.EscapeMarkup() ?? "[dim]not sent[/]");
        AnsiConsole.Write(metadata);

        ShowMessages(snapshot.Messages, full ? "Messages Sent" : "Message Preview", full);

        if (full)
            ShowTools(snapshot.Tools);
    }

    private static void ShowMessages(IReadOnlyList<ModelRequestMessage> messages, string title, bool full)
    {
        var table = new Table();
        table.Title($"[green]{title}[/]");
        table.AddColumn("#");
        table.AddColumn("Role");
        table.AddColumn("Chars");
        table.AddColumn(full ? "Content" : "Preview");

        for (var i = 0; i < messages.Count; i++)
        {
            var message = messages[i];
            var content = FormatMessage(message);
            table.AddRow(
                (i + 1).ToString("N0"),
                FormatRole(message).EscapeMarkup(),
                message.Content.Length.ToString("N0"),
                (full ? content : Preview(content, 220)).EscapeMarkup());
        }

        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();
    }

    private static void ShowMessages(List<ChatMessage> history, string title, bool full)
    {
        var messages = history
            .Select(message => new ModelRequestMessage(message.Role, message.Content, message.ToolCallId))
            .ToArray();
        ShowMessages(messages, title, full);
    }

    private static void ShowTools(IReadOnlyList<ModelRequestTool> tools)
    {
        if (tools.Count == 0)
            return;

        var table = new Table();
        table.Title("[green]Tool Schemas Sent[/]");
        table.AddColumn("Name");
        table.AddColumn("Description");
        table.AddColumn("Parameters");

        foreach (var tool in tools)
            table.AddRow(
                tool.Name.EscapeMarkup(),
                tool.Description.EscapeMarkup(),
                tool.ParametersJson?.EscapeMarkup() ?? "[dim]not sent[/]");

        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();
    }

    private static string FormatRole(ModelRequestMessage message)
    {
        return message.ToolCallId is null ? message.Role : $"{message.Role} ({message.ToolCallId})";
    }

    private static string FormatMessage(ModelRequestMessage message)
    {
        if (message.ToolCalls.Count == 0)
            return message.Content;

        var calls = string.Join('\n', message.ToolCalls.Select(call =>
            $"Tool call {call.Id}: {call.Name}({call.Arguments})"));
        return string.IsNullOrEmpty(message.Content) ? calls : message.Content + "\n" + calls;
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
