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
        // Find the first non-flag argument that isn't a value for a flag
        string? firstNonFlag = null;
        bool skipNext = false;
        foreach (var arg in args)
        {
            if (skipNext) { skipNext = false; continue; }
            if (arg == "--tools") { skipNext = true; continue; }
            if (arg.StartsWith("--tools=")) continue;
            if (arg == "--model") { skipNext = true; continue; }
            if (arg.StartsWith("--model=")) continue;
            if (arg == "--base-url") { skipNext = true; continue; }
            if (arg.StartsWith("--base-url=")) continue;
            if (arg == "--api-key") { skipNext = true; continue; }
            if (arg.StartsWith("--api-key=")) continue;
            if (arg == "--system") { skipNext = true; continue; }
            if (arg.StartsWith("--system=")) continue;
            if (arg == "--thinking") { skipNext = true; continue; }
            if (arg.StartsWith("--thinking=")) continue;
            if (arg == "-m" || arg == "-u" || arg == "-k" || arg == "-s") { skipNext = true; continue; }
            if (arg == "-n" || arg == "--no-stream") continue;
            if (arg == "--help" || arg == "-h")
            {
                ShowHelp();
                return 0;
            }
            if (arg.StartsWith("-m=")) continue;
            if (arg.StartsWith("-u=")) continue;
            if (arg.StartsWith("-k=")) continue;
            if (arg.StartsWith("-s=")) continue;
            if (!arg.StartsWith("-"))
            {
                firstNonFlag = arg;
                break;
            }
        }

        if (string.IsNullOrEmpty(firstNonFlag))
            return await RunChat(args);

        var cmdIndex = Array.IndexOf(args, firstNonFlag);
        var cmd = firstNonFlag.ToLowerInvariant();
        var rest = args.Skip(cmdIndex + 1).Concat(args.Take(cmdIndex)).ToArray();

        switch (cmd)
        {
            case "chat":
                return await RunChat(rest);
            case "complete":
                return await RunComplete(rest);
            case "config":
                return await RunConfig(rest);
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
        AnsiConsole.WriteLine("Usage: pico [--tools bash,edit,read,write] [--model name] [--base-url url]");
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
        AnsiConsole.WriteLine("  --tools <list>         Enable tools: bash,edit,read,write (comma-separated)");
        AnsiConsole.WriteLine("  --thinking <true|false> Enable model thinking/reasoning");
        AnsiConsole.WriteLine();
        AnsiConsole.WriteLine("Environment variables:");
        AnsiConsole.WriteLine("  AGENT_BASE_URL  - OpenAI-compatible API base URL");
        AnsiConsole.WriteLine("  AGENT_API_KEY   - API key (optional for local models)");
        AnsiConsole.WriteLine("  AGENT_MODEL     - Model name");
        AnsiConsole.WriteLine("  AGENT_TOOLS     - Comma-separated list of tools to enable");
        AnsiConsole.WriteLine("  AGENT_THINKING_ENABLED - Enable model thinking/reasoning");
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
                else if (pendingFlag == "--tools") opts.Tools = arg.Split(',').Select(s => s.Trim()).ToArray();
                else if (pendingFlag == "--thinking") opts.Thinking = ParseThinkingArg(arg);
                pendingFlag = null;
                continue;
            }

            if (arg is "-s" or "--system" or "-m" or "--model" or "-u" or "--base-url" or "-k" or "--api-key" or "--tools" or "--thinking")
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
            else if (arg.StartsWith("--thinking "))
                opts.Thinking = ParseThinkingArg(arg["--thinking ".Length..].Trim());
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
            else if (arg.StartsWith("--tools="))
                opts.Tools = arg[8..].Split(',').Select(s => s.Trim()).ToArray();
            else if (arg.StartsWith("--thinking="))
                opts.Thinking = ParseThinkingArg(arg["--thinking=".Length..].Trim());
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
        if (opts.Thinking.HasValue)
            config.ThinkingEnabled = opts.Thinking;
        var tools = ResolveTools(opts.Tools, config);
        var client = new AgentClient(config, tools);

        var header = $"[green]pico[/] - [dim]{config.Model} @ {config.BaseUrl}[/]";
        if (tools?.Length > 0)
            header += $" [dim]tools: {string.Join(", ", tools)}[/]";
        AnsiConsole.MarkupLine(header);
        AnsiConsole.MarkupLine("Type [bold]/quit[/] or [bold]/exit[/] to quit, [bold]/clear[/] to reset conversation, [bold]/sys <prompt>[/] to change system prompt, [bold]/help[/] for help.");
        AnsiConsole.MarkupLine("[dim]Press Ctrl+C to exit.[/]\n");

        var history = new List<ChatMessage>();

        if (!string.IsNullOrEmpty(opts.SystemPrompt))
            history.Add(new ChatMessage("system", opts.SystemPrompt));
        else if (tools?.Length > 0)
        {
            // Default system prompt for tool-enabled mode
            var descriptions = new Dictionary<string, string>
            {
                { "bash", "Run a shell command (ls, cat, grep, find, etc.)" },
                { "read", "Read file contents" },
                { "edit", "Replace exact text in a file" },
                { "write", "Create or overwrite a file" }
            };
            var toolList = tools.Select(t => descriptions.TryGetValue(t, out var d) ? d : t)
                .Aggregate((a, b) => $"{a}, {b}");
            history.Add(new ChatMessage("system",
                $"You are a helpful assistant with access to these tools: {toolList}. " +
                "Use them when the user's request requires it. Think before acting — explain your plan first."));
        }

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
                else if (trimmed == "/help")
                {
                    ShowHelp();
                }
                else
                {
                    AnsiConsole.MarkupLine($"[yellow]Unknown command: {trimmed}[/]");
                }
                continue;
            }

            history.Add(new ChatMessage("user", trimmed));
            AnsiConsole.MarkupLine($"[blue]user>[/] {trimmed}");

            try
            {
                if (tools?.Length > 0)
                {
                    AnsiConsole.Write("[green]agent>[/] ");
                    var (response, tokenCount) = await client.ChatWithToolsAsync(history);
                    AnsiConsole.MarkupLine($"[dim]\n({tokenCount} tokens)[/]\n");
                }
                else if (opts.NoStream || (config.ThinkingEnabled ?? false) && !IsOModel(config.Model))
                {
                    // Use non-streaming when thinking is enabled on non-OpenAI models
                    // because reasoning is in a separate response field the SDK can't stream
                    var useNonStream = !opts.NoStream && (config.ThinkingEnabled ?? false);
                    if (useNonStream)
                        AnsiConsole.MarkupLine("[dim](thinking enabled, using non-streaming mode)[/]");
                    AnsiConsole.Write("[green]agent>[/] ");
                    var (response, reasoning) = await client.ChatAsync(history);
                    if (!string.IsNullOrEmpty(reasoning))
                    {
                        AnsiConsole.MarkupLine("");
                        AnsiConsole.MarkupLine("[dim]Thinking:[/]");
                        foreach (var line in reasoning.Trim().Split('\n'))
                            AnsiConsole.MarkupLine("[dim]  " + line.EscapeMarkup() + "[/]");
                        AnsiConsole.MarkupLine("");
                    }
                    AnsiConsole.MarkupLine(response);
                    history.Add(new ChatMessage("assistant", response));
                }
                else
                {
                    AnsiConsole.Write("[green]agent>[/] ");
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

    private static bool ParseThinkingArg(string arg)
    {
        return arg.ToLowerInvariant() is "true" or "1" or "yes";
    }

    private static bool IsOModel(string model)
    {
        return model.StartsWith("o1", StringComparison.OrdinalIgnoreCase) ||
               model.StartsWith("o3", StringComparison.OrdinalIgnoreCase);
    }

    private static string[]? ResolveTools(string[]? cliTools, AgentConfig config)
    {
        if (cliTools?.Length > 0)
            return cliTools;

        var envTools = Environment.GetEnvironmentVariable("AGENT_TOOLS");
        if (!string.IsNullOrEmpty(envTools))
            return envTools.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var fileTools = config.Tools;
        return fileTools;
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
        if (opts.Thinking.HasValue)
            config.ThinkingEnabled = opts.Thinking;
        var client = new AgentClient(config);

        AnsiConsole.MarkupLine($"[dim]Model: {config.Model}[/]");
        AnsiConsole.MarkupLine($"[dim]Endpoint: {config.BaseUrl}[/]");
        AnsiConsole.Write("[dim]Prompt:[/] ");
        AnsiConsole.WriteLine(prompt.Trim());
        AnsiConsole.WriteLine();

        try
        {
            var (response, reasoning) = await client.CompleteAsync(prompt, opts.SystemPrompt);
            if (!string.IsNullOrEmpty(reasoning))
            {
                AnsiConsole.MarkupLine("[dim]Thinking:[/]");
                foreach (var line in reasoning.Trim().Split('\n'))
                    AnsiConsole.MarkupLine("[dim]  " + line.EscapeMarkup() + "[/]");
                AnsiConsole.MarkupLine("");
            }
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
            ("AGENT_THINKING_ENABLED", "Thinking", config.ThinkingEnabled?.ToString() ?? "default"),
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
    public string[]? Tools { get; set; }
    public bool? Thinking { get; set; }
}
