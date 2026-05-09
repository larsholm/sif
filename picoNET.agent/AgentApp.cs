using System.Text;
using Spectre.Console;
using ConsoleMarkdownRenderer;

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
                AnsiConsole.MarkupLine($"[yellow]Unknown command: {cmd.EscapeMarkup()}[/]");
                ShowHelp();
                return 1;
        }
    }

    private static void ShowHelp()
    {
        AnsiConsole.MarkupLine("[green]pico[/] - AI agent for local OpenAI-compatible models");
        AnsiConsole.WriteLine();
        AnsiConsole.WriteLine("Usage: pico [--tools bash,edit,read,write,context] [--model name] [--base-url url]");
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
        AnsiConsole.WriteLine("  -t, --temperature <v>  Sampling temperature");
        AnsiConsole.WriteLine("  -max, --max-tokens <v> Max tokens to generate");
        AnsiConsole.WriteLine("  -n, --no-stream        Disable streaming output");
        AnsiConsole.WriteLine("  --tools <list>         Enable tools: bash,edit,read,write,context (comma-separated)");
        AnsiConsole.WriteLine("  --thinking <true|false> Enable model thinking/reasoning");
        AnsiConsole.WriteLine();
        AnsiConsole.WriteLine("Environment variables:");
        AnsiConsole.WriteLine("  AGENT_BASE_URL  - OpenAI-compatible API base URL");
        AnsiConsole.WriteLine("  AGENT_API_KEY   - API key (optional for local models)");
        AnsiConsole.WriteLine("  AGENT_MODEL     - Model name");
        AnsiConsole.WriteLine("  AGENT_TOOLS     - Comma-separated list of tools to enable");
        AnsiConsole.WriteLine("  AGENT_THINKING_ENABLED - Enable model thinking/reasoning");
        AnsiConsole.WriteLine("  AGENT_MAX_TOKENS - Max tokens to generate");
        AnsiConsole.WriteLine("  AGENT_TEMPERATURE - Sampling temperature");
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
                else if (pendingFlag == "-t" || pendingFlag == "--temperature") opts.Temperature = float.TryParse(arg, out var t) ? t : null;
                else if (pendingFlag == "-max" || pendingFlag == "--max-tokens") opts.MaxTokens = int.TryParse(arg, out var m) ? m : null;
                else if (pendingFlag == "--tools") opts.Tools = arg.Split(',').Select(s => s.Trim()).ToArray();
                else if (pendingFlag == "--thinking") opts.Thinking = ParseThinkingArg(arg);
                pendingFlag = null;
                continue;
            }

            if (arg is "-s" or "--system" or "-m" or "--model" or "-u" or "--base-url" or "-k" or "--api-key" or "--tools" or "--thinking" or "-t" or "--temperature" or "-max" or "--max-tokens")
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
            else if (arg.StartsWith("-t="))
                opts.Temperature = float.TryParse(arg[3..], out var t) ? t : null;
            else if (arg.StartsWith("--temperature="))
                opts.Temperature = float.TryParse(arg[14..], out var t2) ? t2 : null;
            else if (arg.StartsWith("-max="))
                opts.MaxTokens = int.TryParse(arg[5..], out var m) ? m : null;
            else if (arg.StartsWith("--max-tokens="))
                opts.MaxTokens = int.TryParse(arg[13..], out var m2) ? m2 : null;
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
        var config = AgentConfig.Build(opts.BaseUrl, opts.ApiKey, opts.Model, opts.Temperature, opts.MaxTokens);
        if (opts.Thinking.HasValue)
            config.ThinkingEnabled = opts.Thinking;

        await using var mcpService = new McpService();
        if (config.McpServers?.Count > 0)
        {
            AnsiConsole.MarkupLine("[dim]Connecting to MCP servers...[/]");
            await mcpService.InitializeAsync(config.McpServers);
        }

        var tools = ResolveTools(opts.Tools, config);
        var client = new AgentClient(config, tools, mcpService);

        var header = $"[green]pico[/] - [dim]{config.Model} @ {config.BaseUrl}[/]";
        if (tools?.Length > 0)
            header += $" [dim]tools: {string.Join(", ", tools)}[/]";
        var mcpToolsCount = mcpService.GetTools().Count;
        if (mcpToolsCount > 0)
            header += $" [dim]mcp-tools: {mcpToolsCount}[/]";
        
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
                { "write", "Create or overwrite a file" },
                { "context", "Store and search large tool results out-of-band" },
                { "ctx", "Store and search large tool results out-of-band" }
            };
            var toolList = tools.Select(t => descriptions.TryGetValue(t, out var d) ? d : t)
                .Aggregate((a, b) => $"{a}, {b}");
            history.Add(new ChatMessage("system",
                $"You are a helpful assistant with access to these tools: {toolList}. " +
                $"Current working directory: {Environment.CurrentDirectory}. " +
                "Always use absolute paths when referring to files." +
                "\n\nUse tools proactively: " +
                "\n- Use 'bash' with 'ls' to list files, 'grep' to search" +
                "\n- Use 'read' to view file contents" +
                "\n- Use 'edit' to modify files" +
                "\n- Use 'write' to create new files" +
                "\n- Use 'ctx_search' and 'ctx_read' when a tool result says large context was stored" +
                "\n- Use 'ctx_index' for large pasted text or generated data that should be searchable later" +
                "\nThink before acting — explain your plan first."));
        }

        bool running = true;
        var files = new Lazy<List<string>>(GetFiles);

        while (running)
        {
            string? input = await ReadChatInputAsync(files);

            if (input is null) break;
            if (string.IsNullOrWhiteSpace(input)) continue;

            var trimmed = input.Trim();

            if (trimmed.StartsWith("/"))
            {
                if (trimmed == "/q" || trimmed == "/quit" || trimmed == "/exit")
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
                    AnsiConsole.MarkupLine($"[yellow]Unknown command: {trimmed.EscapeMarkup()}[/]");
                }
                continue;
            }

            history.Add(new ChatMessage("user", trimmed));

            try
            {
                if (tools?.Length > 0)
                {
                    AnsiConsole.Write(new Markup("[green]agent>[/] "));
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
                    AnsiConsole.Write(new Markup("[green]agent>[/] "));
                    var (response, reasoning) = await client.ChatAsync(history);
                    if (!string.IsNullOrEmpty(reasoning))
                    {
                        AnsiConsole.MarkupLine("");
                        AnsiConsole.MarkupLine("[dim]Thinking:[/]");
                        foreach (var line in reasoning.Trim().Split('\n'))
                            AnsiConsole.MarkupLine("[dim]  " + line.EscapeMarkup() + "[/]");
                        AnsiConsole.MarkupLine("");
                    }
                    await UiService.DisplayMarkdown(response);
                    AnsiConsole.WriteLine();
                    history.Add(new ChatMessage("assistant", response));
                }
                else
                {
                    AnsiConsole.Write(new Markup("[green]agent>[/] "));
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
        // CLI flag takes highest priority
        if (cliTools?.Length > 0)
            return cliTools;

        // Environment variable
        var envTools = Environment.GetEnvironmentVariable("AGENT_TOOLS");
        if (!string.IsNullOrEmpty(envTools))
            return envTools.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // Config file
        if (config.Tools?.Length > 0)
            return config.Tools;

        // Default: always enable tools
        return new[] { "bash", "read", "edit", "write", "context", "debug" };
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

        var config = AgentConfig.Build(opts.BaseUrl, opts.ApiKey, opts.Model, opts.Temperature, opts.MaxTokens);
        if (opts.Thinking.HasValue)
            config.ThinkingEnabled = opts.Thinking;

        await using var mcpService = new McpService();
        if (config.McpServers?.Count > 0)
        {
            await mcpService.InitializeAsync(config.McpServers);
        }

        var tools = ResolveTools(opts.Tools, config);
        var client = new AgentClient(config, tools, mcpService);

        AnsiConsole.MarkupLine($"[dim]Model: {config.Model}[/]");
        if (tools?.Length > 0)
            AnsiConsole.MarkupLine($"[dim]Tools: {string.Join(", ", tools)}[/]");
        var mcpToolsCount = mcpService.GetTools().Count;
        if (mcpToolsCount > 0)
            AnsiConsole.MarkupLine($"[dim]MCP Tools: {mcpToolsCount}[/]");
        AnsiConsole.MarkupLine($"[dim]Endpoint: {config.BaseUrl}[/]");
        AnsiConsole.Write(new Markup("[dim]Prompt:[/] "));
        AnsiConsole.WriteLine(prompt.Trim());
        AnsiConsole.WriteLine();

        try
        {
            if (tools?.Length > 0)
            {
                var history = new List<ChatMessage>();
                if (!string.IsNullOrEmpty(opts.SystemPrompt))
                    history.Add(new ChatMessage("system", opts.SystemPrompt));
                history.Add(new ChatMessage("user", prompt));
                var (response, tokenCount) = await client.ChatWithToolsAsync(history);
                AnsiConsole.MarkupLine($"\n[dim]({tokenCount} tokens)[/]");
            }
            else
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
                await UiService.DisplayMarkdown(response);
                AnsiConsole.WriteLine();
            }
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] {ex.Message.EscapeMarkup()}");
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
                    config.ApplyValue(parts[0], parts[1]);
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
            ("AGENT_TOOLS", "Tools", config.Tools is { Length: > 0 } ? string.Join(",", config.Tools) : "default"),
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

    private async Task<string?> ReadChatInputAsync(Lazy<List<string>> files)
    {
        var sb = new StringBuilder();
        int cursor = 0;
        AnsiConsole.Write(new Markup("[blue]user>[/] "));

        void Redraw()
        {
            // Clear current line and move to start
            Console.Write("\r" + new string(' ', Math.Max(0, Console.WindowWidth - 1)) + "\r");
            
            // This is a simplified redraw that doesn't handle multi-line wrapping perfectly
            // but handles explicit newlines.
            AnsiConsole.Write(new Markup("[blue]user>[/] "));
            var lines = sb.ToString().Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                Console.Write(lines[i]);
                if (i < lines.Length - 1) Console.Write("\n         "); // Offset for prompt
            }

            // Move cursor back to the correct position
            // This is complex for multi-line. For now, we'll just put it at the end 
            // if we've done a full redraw, or handle single-line movements.
            var textUpToCursor = sb.ToString().Substring(0, cursor);
            int cursorLine = textUpToCursor.Count(c => c == '\n');
            int cursorCol = cursor - textUpToCursor.LastIndexOf('\n') - 1;
            if (textUpToCursor.LastIndexOf('\n') == -1) cursorCol = cursor;

            int totalLines = sb.ToString().Count(c => c == '\n');
            if (totalLines > cursorLine)
            {
                AnsiConsole.Cursor.MoveUp(totalLines - cursorLine);
            }
            Console.Write("\r");
            for (int i = 0; i < cursorCol + 7; i++) Console.Write("\x1b[C"); // Move right (prompt is 7 chars "user> ")
        }

        while (true)
        {
            if (!Console.KeyAvailable)
            {
                await Task.Delay(20);
                continue;
            }

            var key = Console.ReadKey(intercept: true);

            if (key.Key == ConsoleKey.Enter)
            {
                if (key.Modifiers.HasFlag(ConsoleModifiers.Alt))
                {
                    sb.Insert(cursor, '\n');
                    cursor++;
                    Console.WriteLine();
                    Console.Write("        "); // Prompt indent
                    Redraw();
                    continue;
                }
                Console.WriteLine();
                return sb.ToString();
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (cursor > 0)
                {
                    bool isNewline = sb[cursor - 1] == '\n';
                    sb.Remove(cursor - 1, 1);
                    cursor--;
                    if (isNewline)
                    {
                        AnsiConsole.Cursor.MoveUp(1);
                        Redraw();
                    }
                    else
                    {
                        Console.Write("\b \b");
                        if (cursor < sb.Length)
                        {
                            var remaining = sb.ToString()[cursor..];
                            Console.Write(remaining + " ");
                            for (int i = 0; i <= remaining.Length; i++) Console.Write("\b");
                        }
                    }
                }
                continue;
            }

            if (key.Key == ConsoleKey.Delete)
            {
                if (cursor < sb.Length)
                {
                    sb.Remove(cursor, 1);
                    Redraw();
                }
                continue;
            }

            if (key.Key == ConsoleKey.Home)
            {
                cursor = 0;
                Redraw();
                continue;
            }

            if (key.Key == ConsoleKey.End)
            {
                cursor = sb.Length;
                Redraw();
                continue;
            }

            if (key.Key == ConsoleKey.LeftArrow)
            {
                if (cursor > 0)
                {
                    cursor--;
                    Redraw();
                }
                continue;
            }

            if (key.Key == ConsoleKey.RightArrow)
            {
                if (cursor < sb.Length)
                {
                    cursor++;
                    Redraw();
                }
                continue;
            }

            if (key.Modifiers.HasFlag(ConsoleModifiers.Control) && key.Key == ConsoleKey.C)
            {
                return null;
            }

            if (key.Key == ConsoleKey.Tab)
            {
                // Find the word starting with # before the cursor
                string currentText = sb.ToString();
                int hashIdx = -1;
                for (int i = cursor - 1; i >= 0; i--)
                {
                    if (currentText[i] == '#') { hashIdx = i; break; }
                    if (char.IsWhiteSpace(currentText[i])) break;
                }

                if (hashIdx >= 0)
                {
                    string prefix = currentText.Substring(hashIdx + 1, cursor - (hashIdx + 1));
                    var matches = files.Value
                        .Where(f => f.Contains(prefix, StringComparison.OrdinalIgnoreCase))
                        .ToList();

                    if (matches.Count > 0)
                    {
                        string? choice = null;
                        if (matches.Count == 1)
                        {
                            choice = matches[0];
                        }
                        else
                        {
                            choice = AnsiConsole.Prompt(
                                new SelectionPrompt<string>()
                                    .Title("Select a file")
                                    .PageSize(10)
                                    .AddChoices(matches));
                        }

                        if (choice != null)
                        {
                            sb.Remove(hashIdx + 1, prefix.Length);
                            sb.Insert(hashIdx + 1, choice);
                            cursor = hashIdx + 1 + choice.Length;
                            Redraw();
                        }
                    }
                }
                continue;
            }
            else if (!char.IsControl(key.KeyChar))
            {
                sb.Insert(cursor, key.KeyChar);
                cursor++;
                if (sb.ToString().Contains('\n'))
                {
                    Redraw();
                }
                else
                {
                    Console.Write(key.KeyChar);
                    if (cursor < sb.Length)
                    {
                        var remaining = sb.ToString()[cursor..];
                        Console.Write(remaining);
                        for (int i = 0; i < remaining.Length; i++) Console.Write("\b");
                    }
                }
            }
        }
    }

    private List<string> GetFiles()
    {
        var root = Environment.CurrentDirectory;
        var files = new List<string>();
        try
        {
            var excludeDirs = new HashSet<string> { ".git", "bin", "obj", "node_modules", ".vs", ".vscode", ".gemini" };

            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(root, file);
                var parts = relative.Split(Path.DirectorySeparatorChar);
                if (!parts.Any(p => excludeDirs.Contains(p) || p.StartsWith(".")))
                {
                    files.Add(relative.Replace(Path.DirectorySeparatorChar, '/'));
                }
            }
        }
        catch { }
        return files;
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
    public float? Temperature { get; set; }
    public int? MaxTokens { get; set; }
}
