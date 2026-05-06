using System.Text;
using Spectre.Console;

namespace picoNET.agent;

/// <summary>
/// Application entry point. Parses CLI arguments and dispatches to the right action.
/// </summary>
internal class AgentApp
{
    public async Task<int> Run(string[] args)
    {
        if (args.Length == 0)
        {
            return await RunChat(Array.Empty<string>());
        }

        var cmd = args[0].ToLowerInvariant();
        var rest = args.Skip(1).ToArray();

        switch (cmd)
        {
            case "chat":
                return await RunChat(rest);
            case "complete":
                return await RunComplete(rest);
            case "config":
                return await RunConfig(rest);
            case "--help":
            case "-h":
                ShowHelp();
                return 0;
            default:
                AnsiConsole.MarkupLine($"[yellow]Unknown command: {cmd}[/]");
                ShowHelp();
                return 1;
        }
    }

    private static void ShowHelp()
    {
        AnsiConsole.MarkupLine("[green]pico[/] - AI agent for local OpenAI-compatible models");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("Usage: [bold]pico [options...][/]");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("  (no args)     Start an interactive chat session");
        AnsiConsole.MarkupLine("  [bold]complete[/]  Run a one-off prompt and exit");
        AnsiConsole.MarkupLine("  [bold]config[/]  Show or set configuration");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("Options (all commands):");
        AnsiConsole.WriteLine("  -m, --model <name>     Model to use");
        AnsiConsole.WriteLine("  -u, --base-url <url>   API base URL");
        AnsiConsole.WriteLine("  -k, --api-key <key>    API key");
        AnsiConsole.WriteLine("  -s, --system <text>    System prompt");
        AnsiConsole.WriteLine("  -n, --no-stream        Disable streaming output");
        AnsiConsole.WriteLine();
        AnsiConsole.WriteLine("Environment variables:");
        AnsiConsole.WriteLine("  AGENT_BASE_URL  - OpenAI-compatible API base URL");
        AnsiConsole.WriteLine("  AGENT_API_KEY   - API key (optional for local models)");
        AnsiConsole.WriteLine("  AGENT_MODEL     - Model name");
    }

    private static CliArgs ParseArgs(string[] args)
    {
        var opts = new CliArgs();
        bool nextIsValue = false;
        string? pendingFlag = null;

        foreach (var arg in args)
        {
            if (nextIsValue)
            {
                nextIsValue = false;
                if (pendingFlag == "-s" || pendingFlag == "--system") opts.SystemPrompt = arg;
                else if (pendingFlag == "-m" || pendingFlag == "--model") opts.Model = arg;
                else if (pendingFlag == "-u" || pendingFlag == "--base-url") opts.BaseUrl = arg;
                else if (pendingFlag == "-k" || pendingFlag == "--api-key") opts.ApiKey = arg;
                pendingFlag = null;
                continue;
            }

            if (arg is "-s" or "--system" or "-m" or "--model" or "-u" or "--base-url" or "-k" or "--api-key")
            {
                nextIsValue = true;
                pendingFlag = arg;
                continue;
            }

            if (arg.StartsWith("-s ") || arg.StartsWith("--system "))
                opts.SystemPrompt = arg[(arg.StartsWith("-s ") ? 3 : 9)..].Trim();
            else if (arg.StartsWith("-m ") || arg.StartsWith("--model "))
                opts.Model = arg[(arg.StartsWith("-m ") ? 3 : 8)..].Trim();
            else if (arg.StartsWith("-u ") || arg.StartsWith("--base-url "))
                opts.BaseUrl = arg[(arg.StartsWith("-u ") ? 3 : 11)..].Trim();
            else if (arg.StartsWith("-k ") || arg.StartsWith("--api-key "))
                opts.ApiKey = arg[(arg.StartsWith("-k ") ? 3 : 11)..].Trim();
            else if (arg is "-n" or "--no-stream")
                opts.NoStream = true;
            else if (arg.StartsWith("-s="))
                opts.SystemPrompt = arg[3..];
            else if (arg.StartsWith("--system="))
                opts.SystemPrompt = arg[9..];
            else if (arg.StartsWith("-m="))
                opts.Model = arg[3..];
            else if (arg.StartsWith("--model="))
                opts.Model = arg[8..];
            else if (arg.StartsWith("-u="))
                opts.BaseUrl = arg[3..];
            else if (arg.StartsWith("--base-url="))
                opts.BaseUrl = arg[11..];
            else if (arg.StartsWith("-k="))
                opts.ApiKey = arg[3..];
            else if (arg.StartsWith("--api-key="))
                opts.ApiKey = arg[11..];
            else if (!arg.StartsWith("-"))
            {
                opts.Positional ??= new List<string>();
                opts.Positional.Add(arg);
            }
        }
        return opts;
    }

    private async Task<int> RunChat(string[] args)
    {
        var opts = ParseArgs(args);
        var config = AgentConfig.Build(opts.BaseUrl, opts.ApiKey, opts.Model);
        var client = new AgentClient(config);

        AnsiConsole.MarkupLine($"[green]pico[/] - [dim]{config.Model} @ {config.BaseUrl}[/]");
        AnsiConsole.MarkupLine("Type [bold]/quit[/] or [bold]/exit[/] to quit, [bold]/clear[/] to reset conversation, [bold]/sys <prompt>[/] to change system prompt.");
        AnsiConsole.MarkupLine("[dim]Press Ctrl+C to exit.[/]\n");

        var history = new List<ChatMessage>();

        if (!string.IsNullOrEmpty(opts.SystemPrompt))
            history.Add(new ChatMessage("system", opts.SystemPrompt));

        bool running = true;
        while (running)
        {
            AnsiConsole.Write("[blue]user>[/] ");
            string? input = await Console.In.ReadLineAsync();

            if (input is null) break;
            if (input.Trim() == "") continue;

            var trimmed = input.Trim();

            if (trimmed.StartsWith("/"))
            {
                if (trimmed == "/quit" || trimmed == "/exit")
                {
                    running = false;
                }
                else if (trimmed == "/clear")
                {
                    var sys = history.FirstOrDefault(m => m.Role == "system");
                    history.Clear();
                    if (sys != null) history.Add(sys);
                    AnsiConsole.MarkupLine("[dim]Conversation cleared.[/]\n");
                }
                else if (trimmed.StartsWith("/sys "))
                {
                    var newSys = trimmed[5..].Trim();
                    var idx = history.FindIndex(m => m.Role == "system");
                    if (idx >= 0) history[idx] = new ChatMessage("system", newSys);
                    else history.Insert(0, new ChatMessage("system", newSys));
                    AnsiConsole.MarkupLine("[dim]System prompt updated.[/]\n");
                }
                else
                {
                    AnsiConsole.MarkupLine($"[yellow]Unknown command: {trimmed}[/]");
                }
                continue;
            }

            history.Add(new ChatMessage("user", trimmed));
            AnsiConsole.MarkupLine($"[blue]user>[/] {trimmed}");
            AnsiConsole.Write("[green]agent>[/] ");

            try
            {
                if (opts.NoStream)
                {
                    var response = await client.ChatAsync(history);
                    AnsiConsole.MarkupLine(response);
                    history.Add(new ChatMessage("assistant", response));
                }
                else
                {
                    var (response, tokenCount) = await client.ChatStreamingAsync(history);
                    history.Add(new ChatMessage("assistant", response));
                    AnsiConsole.MarkupLine($"[dim]\n({tokenCount} tokens)[/]\n");
                }
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]Error:[/] {ex.Message}\n");
                history.RemoveAt(history.Count - 1);
            }
        }

        AnsiConsole.MarkupLine("\n[dim]Goodbye![/]");
        return 0;
    }

    private async Task<int> RunComplete(string[] args)
    {
        var opts = ParseArgs(args);
        string? prompt = opts.Positional?.FirstOrDefault();

        if (string.IsNullOrEmpty(prompt))
        {
            if (Console.IsInputRedirected)
            {
                prompt = await Console.In.ReadToEndAsync();
            }
            else
            {
                AnsiConsole.MarkupLine("[yellow]Error:[/] Provide a prompt argument or pipe input via stdin.");
                return 1;
            }
        }

        if (prompt == "-")
            prompt = await Console.In.ReadToEndAsync();

        if (string.IsNullOrEmpty(prompt))
        {
            AnsiConsole.MarkupLine("[yellow]Error:[/] No prompt provided.");
            return 1;
        }

        var config = AgentConfig.Build(opts.BaseUrl, opts.ApiKey, opts.Model);
        var client = new AgentClient(config);

        AnsiConsole.MarkupLine($"[dim]Model: {config.Model}[/]");
        AnsiConsole.MarkupLine($"[dim]Endpoint: {config.BaseUrl}[/]");
        AnsiConsole.Write("[dim]Prompt:[/] ");
        AnsiConsole.WriteLine(prompt.Trim());
        AnsiConsole.WriteLine();

        try
        {
            var response = await client.CompleteAsync(prompt, opts.SystemPrompt);
            AnsiConsole.MarkupLine("[green]Response:[/]");
            AnsiConsole.MarkupLine(response);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] {ex.Message}");
            return 1;
        }

        return 0;
    }

    private async Task<int> RunConfig(string[] args)
    {
        var config = AgentConfig.Load();

        foreach (var arg in args)
        {
            if (arg.StartsWith("-s=") || arg.StartsWith("--set="))
            {
                var value = arg[(arg.StartsWith("-s=") ? 3 : 6)..];
                var parts = value.Split('=', 2);
                if (parts.Length == 2)
                {
                    config.Values[parts[0].Trim().ToUpperInvariant()] = parts[1];
                    config.Save();
                    AnsiConsole.MarkupLine("[green]Configuration updated.[/]\n");
                }
            }
        }

        var table = new Table();
        table.AddColumn("Key");
        table.AddColumn("Value");
        table.AddColumn("Source");

        var sources = new[]
        {
            ("AGENT_BASE_URL", "Base URL", config.BaseUrl),
            ("AGENT_API_KEY", "API Key", config.ApiKey),
            ("AGENT_MODEL", "Model", config.Model),
            ("AGENT_MAX_TOKENS", "Max Tokens", config.MaxTokens?.ToString() ?? "default"),
            ("AGENT_TEMPERATURE", "Temperature", config.Temperature?.ToString() ?? "default"),
        };

        foreach (var (key, label, value) in sources)
        {
            var masked = key.Contains("KEY", StringComparison.OrdinalIgnoreCase)
                ? (string.IsNullOrEmpty(value) ? "" : new string('*', Math.Min(value.Length, 4)))
                : value ?? "";
            table.AddRow(label, masked, $"[dim]{key}[/]");
        }

        AnsiConsole.Write(table);
        return 0;
    }
}

internal class CliArgs
{
    public List<string>? Positional { get; set; }
    public string? SystemPrompt { get; set; }
    public string? Model { get; set; }
    public string? BaseUrl { get; set; }
    public string? ApiKey { get; set; }
    public bool NoStream { get; set; }
}
