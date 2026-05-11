using System.Text;
using System.Text.Json;
using System.Diagnostics;
using Spectre.Console;
using ConsoleMarkdownRenderer;

namespace sif.agent;

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
            if (arg == "--temperature") { skipNext = true; continue; }
            if (arg.StartsWith("--temperature=")) continue;
            if (arg == "--max-tokens") { skipNext = true; continue; }
            if (arg.StartsWith("--max-tokens=")) continue;
            if (arg == "-m" || arg == "-u" || arg == "-k" || arg == "-s" || arg == "-t" || arg == "-max") { skipNext = true; continue; }
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
            if (arg.StartsWith("-t=")) continue;
            if (arg.StartsWith("-max=")) continue;
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
            case "setup":
                return await RunSetup(rest);
            case "uninstall":
                return await RunUninstall(rest);
            default:
                AnsiConsole.MarkupLine($"[yellow]Unknown command: {cmd.EscapeMarkup()}[/]");
                ShowHelp();
                return 1;
        }
    }

    private static void ShowHelp()
    {
        AnsiConsole.MarkupLine("[green]sif[/] - AI agent for local OpenAI-compatible models");
        AnsiConsole.WriteLine();
        AnsiConsole.WriteLine("Usage: sif [--tools bash,edit,read,write,sleep,serve,context] [--model name] [--base-url url]");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("  (no args)     Start an interactive chat session");
        AnsiConsole.MarkupLine("  [bold]complete[/]  Run a one-off prompt and exit");
        AnsiConsole.MarkupLine("  [bold]config[/]  Show or set configuration");
        AnsiConsole.MarkupLine("  [bold]setup[/]  Run the first-launch setup wizard");
        AnsiConsole.MarkupLine("  [bold]uninstall[/]  Remove the global tool and VS Code extension");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("Options (all commands):");
        AnsiConsole.WriteLine("  -m, --model <name>     Model to use");
        AnsiConsole.WriteLine("  -u, --base-url <url>   API base URL");
        AnsiConsole.WriteLine("  -k, --api-key <key>    API key");
        AnsiConsole.WriteLine("  -s, --system <text>    System prompt");
        AnsiConsole.WriteLine("  -t, --temperature <v>  Sampling temperature");
        AnsiConsole.WriteLine("  -max, --max-tokens <v> Max tokens to generate");
        AnsiConsole.WriteLine("  -n, --no-stream        Disable streaming output");
        AnsiConsole.WriteLine("  --tools <list>         Enable tools: bash,edit,read,write,sleep,serve,context (comma-separated)");
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
        AnsiConsole.WriteLine("  AGENT_COMPACTION_THRESHOLD - Chat-history token threshold for compaction (0 disables)");
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
        if (ShouldRunFirstLaunchSetup(opts))
            await RunSetupWizard(firstLaunch: true);

        var config = AgentConfig.Build(opts.BaseUrl, opts.ApiKey, opts.Model, opts.Temperature, opts.MaxTokens);
        if (opts.Thinking.HasValue)
            config.ThinkingEnabled = opts.Thinking;
        var modelContextLength = await TryApplyModelCompactionThresholdAsync(config);

        await using var mcpService = new McpService();
        if (config.McpServers?.Count > 0)
        {
            AnsiConsole.MarkupLine("[dim]Connecting to MCP servers...[/]");
            await mcpService.InitializeAsync(config.McpServers);
        }

        var tools = ResolveTools(opts.Tools, config);
        var client = new AgentClient(config, tools, mcpService);
        var skills = SkillStore.Load();

        var header = $"[green]sif[/] - [dim]{config.Model} @ {config.BaseUrl}[/]";
        if (tools?.Length > 0)
            header += $" [dim]tools: {string.Join(", ", tools)}[/]";
        if (modelContextLength.HasValue && config.CompactionThreshold > 0)
            header += $" [dim]ctx-window: {modelContextLength.Value:N0}, compact: {config.CompactionThreshold:N0}[/]";
        if (skills.Count > 0)
            header += $" [dim]skills: {skills.Count}[/]";
        var mcpToolsCount = mcpService.GetTools().Count;
        if (mcpToolsCount > 0)
            header += $" [dim]mcp-tools: {mcpToolsCount}[/]";
        if (VscodeContext.IsRunningInVscodeTerminal())
            header += " [dim]vscode[/]";
        
        AnsiConsole.MarkupLine(header);
        AnsiConsole.MarkupLine("Type [bold]/quit[/] or [bold]/exit[/] to quit, [bold]/clear[/] to reset conversation, [bold]/context[/] to inspect context, [bold]/help[/] for help.");
        AnsiConsole.MarkupLine("[dim]Press Ctrl+C to exit.[/]\n");

        var history = new List<ChatMessage>();
        var initialSystemPrompt = BuildInitialSystemPrompt(opts.SystemPrompt, tools, skills);
        if (!string.IsNullOrWhiteSpace(initialSystemPrompt))
            history.Add(new ChatMessage("system", initialSystemPrompt));

        bool running = true;
        var files = new Lazy<List<string>>(GetFiles);
        var inputHistory = new List<string>();

        while (running)
        {
            string? input = await ReadChatInputAsync(files, inputHistory);

            if (input is null) break;
            if (string.IsNullOrWhiteSpace(input)) continue;

            var trimmed = input.Trim();
            if (inputHistory.Count == 0 || inputHistory[^1] != trimmed)
                inputHistory.Add(trimmed);

            if (trimmed.StartsWith("/"))
            {
                if (trimmed == "/q" || trimmed == "/quit" || trimmed == "/exit")
                {
                    running = false;
                }
                else if (trimmed == "/clear")
                {
                    ClearChatHistory(history);
                    AnsiConsole.MarkupLine("[dim]Conversation cleared.[/]\n");
                }
                else if (trimmed.StartsWith("/sys "))
                {
                    var newSys = trimmed[5..].Trim();
                    var effectiveSys = SkillStore.BuildSystemPrompt(newSys, skills);
                    var idx = history.FindIndex(m => m.Role == "system");
                    if (idx >= 0) history[idx] = new ChatMessage("system", effectiveSys);
                    else history.Insert(0, new ChatMessage("system", effectiveSys));
                    AnsiConsole.MarkupLine("[dim]System prompt updated.[/]\n");
                }
                else if (trimmed == "/context" || trimmed.StartsWith("/context "))
                {
                    HandleContextCommand(trimmed, history, tools);
                }
                else if (trimmed == "/debug" || trimmed.StartsWith("/debug "))
                {
                    HandleDebugCommand(trimmed);
                }
                else if (trimmed == "/vscode")
                {
                    ShowVscodeContext();
                }
                else if (trimmed == "/help")
                {
                    ShowChatHelp();
                }
                else
                {
                    AnsiConsole.MarkupLine($"[yellow]Unknown command: {trimmed.EscapeMarkup()}[/]");
                }
                continue;
            }

            var historyContent = PrepareUserMessageForHistory(trimmed, tools);
            history.Add(new ChatMessage("user", historyContent));

            try
            {
                if (tools?.Length > 0)
                {
                    AnsiConsole.Write(new Markup("[green]agent>[/] "));
                    var (response, tokenCount) = await RunWithEscapeCancel(ct => client.ChatWithToolsAsync(history, ct));
                    var ctxEstimate = EstimateContextSize(history);
                    AnsiConsole.MarkupLine($"[dim]\n({tokenCount} tokens, {ctxEstimate} context)[/]\n");
                }
                else if (opts.NoStream || (config.ThinkingEnabled ?? false) && !IsOModel(config.Model))
                {
                    // Use non-streaming when thinking is enabled on non-OpenAI models
                    // because reasoning is in a separate response field the SDK can't stream
                    var useNonStream = !opts.NoStream && (config.ThinkingEnabled ?? false);
                    if (useNonStream)
                        AnsiConsole.MarkupLine("[dim](thinking enabled, using non-streaming mode)[/]");
                    AnsiConsole.Write(new Markup("[green]agent>[/] "));
                    var (response, reasoning) = await RunWithEscapeCancel(ct => client.ChatAsync(history, ct));
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
                    var (response, tokenCount) = await RunWithEscapeCancel(ct => client.ChatStreamingAsync(history, ct));
                    history.Add(new ChatMessage("assistant", response));
                    var ctxEstimate = EstimateContextSize(history);
                    AnsiConsole.MarkupLine($"[dim]\n({tokenCount} tokens, {ctxEstimate} context)[/]\n");
                }
            }
            catch (OperationCanceledException)
            {
                AnsiConsole.MarkupLine("[dim]Cancelled.[/]\n");
                if (history.Count > 0 && history[^1].Role == "user")
                    history.RemoveAt(history.Count - 1);
            }
            catch (Exception ex)
            {
                var debugPath = DebugLog.Save("chat-loop", ex, "during conversation");
                AnsiConsole.MarkupLine($"[red]Error:[/] {ex.Message.EscapeMarkup()}");
                AnsiConsole.MarkupLine($"[dim]Debug saved to {debugPath.EscapeMarkup()}[/]\n");
                if (history.Count > 0 && history[^1].Role == "user")
                    history.RemoveAt(history.Count - 1);
            }

            // Check if we should compact the conversation history
            _ = await MaybeCompactHistoryAsync(history, client, config, tools);
        }

        AnsiConsole.MarkupLine("\n[dim]Goodbye![/]");
        return 0;
    }

    private static async Task<T> RunWithEscapeCancel<T>(Func<CancellationToken, Task<T>> action)
    {
        using var cts = new CancellationTokenSource();
        var actionTask = action(cts.Token);
        var escapeTask = WatchEscapeForCancelAsync(cts);

        try
        {
            var completed = await Task.WhenAny(actionTask, escapeTask);
            if (completed == escapeTask && cts.IsCancellationRequested)
                throw new OperationCanceledException(cts.Token);

            return await actionTask;
        }
        finally
        {
            cts.Cancel();
            try { await escapeTask; } catch { }
        }
    }

    private static string PrepareUserMessageForHistory(string message, string[]? tools)
    {
        message = VscodeContext.AppendToUserMessage(message);

        if (message.Length <= ContextStore.AutoStoreThreshold || !HasContextTool(tools))
            return message;

        var stored = ContextStore.StoreAndDescribe("user message", message);
        AnsiConsole.MarkupLine($"[dim]{stored.Split('\n')[0].EscapeMarkup()}[/]");
        return stored;
    }

    private static bool HasContextTool(string[]? tools)
    {
        return tools?.Any(t => t.Equals("context", StringComparison.OrdinalIgnoreCase) ||
                               t.Equals("ctx", StringComparison.OrdinalIgnoreCase) ||
                               t.Equals("ctx_index", StringComparison.OrdinalIgnoreCase) ||
                               t.Equals("ctx_search", StringComparison.OrdinalIgnoreCase) ||
                               t.Equals("ctx_read", StringComparison.OrdinalIgnoreCase) ||
                               t.Equals("ctx_summarize", StringComparison.OrdinalIgnoreCase) ||
                               t.Equals("ctx_stats", StringComparison.OrdinalIgnoreCase)) == true;
    }

    /// <summary>
    /// When chat history size exceeds CompactionThreshold, call the LLM to summarize
    /// the conversation, store the summary in ContextStore, and replace old history
    /// with system prompt + summary reference + most recent messages.
    /// Returns true if compaction was performed.
    /// </summary>
    private static async Task<bool> MaybeCompactHistoryAsync(List<ChatMessage> history, AgentClient client, AgentConfig config, string[]? tools, CancellationToken cancellationToken = default)
    {
        const int RecentMessageCount = 4;
        const int MaxCompactionChunkChars = 48000;
        const string CompactionSystemMarker = "--- Compacted conversation context ---\n";

        // Compaction disabled if threshold is 0 or negative
        if (config.CompactionThreshold <= 0)
            return false;

        // Need context tool to store summaries
        if (!HasContextTool(tools))
            return false;

        // Need at least a few messages to be worth compacting
        if (history.Count < 5)
            return false;

        // Estimate chat history size in tokens. Stored context is intentionally excluded:
        // compaction moves data there, so counting it would immediately retrigger compaction.
        var chars = history.Sum(m => m.Content.Length);
        var estimatedTokens = chars / 4;

        // Check if we've crossed the threshold
        if (estimatedTokens < config.CompactionThreshold)
            return false;

        // Find the system prompt
        var systemIdx = history.FindIndex(m => m.Role == "system");
        var systemPrompt = systemIdx >= 0 ? history[systemIdx].Content : "";
        var markerIndex = systemPrompt.IndexOf(CompactionSystemMarker, StringComparison.Ordinal);
        var baseSystemPrompt = markerIndex >= 0 ? systemPrompt[..markerIndex] : systemPrompt;

        var nonSystemMessages = history.Where(m => m.Role != "system").ToList();
        var keepCount = Math.Min(RecentMessageCount, nonSystemMessages.Count);
        var recentMessages = nonSystemMessages.TakeLast(keepCount).ToList();

        // Build content to summarize: all non-system messages except the recent ones
        var messagesToSummarize = nonSystemMessages.Take(nonSystemMessages.Count - keepCount).ToList();

        if (messagesToSummarize.Count == 0)
            return false;

        List<string> BuildCompactionChunks()
        {
            var chunks = new List<string>();
            var current = new StringBuilder();

            void Flush()
            {
                if (current.Length == 0)
                    return;

                chunks.Add(current.ToString());
                current.Clear();
            }

            foreach (var msg in messagesToSummarize)
            {
                var formatted = $"[{msg.Role}]\n{msg.Content}\n\n";
                if (formatted.Length > MaxCompactionChunkChars)
                {
                    Flush();
                    for (int offset = 0; offset < formatted.Length; offset += MaxCompactionChunkChars)
                        chunks.Add(formatted.Substring(offset, Math.Min(MaxCompactionChunkChars, formatted.Length - offset)));
                    continue;
                }

                if (current.Length + formatted.Length > MaxCompactionChunkChars)
                    Flush();

                current.Append(formatted);
            }

            Flush();
            return chunks;
        }

        async Task<string> SummarizeChunkAsync(string content, string focus)
        {
            var prompt = $@"Summarize this conversation history for compaction.
Preserve decisions, facts, user preferences, unresolved tasks, code/file changes, tool results, ids, paths, errors, and assumptions needed to continue the conversation.
Focus: {focus}

Conversation:
{content}";

            var (summary, _) = await client.CompleteAsync(
                prompt,
                "You compact chat history. Produce a concise but complete continuation summary. Do not invent facts.");
            return summary.Trim();
        }

        async Task<string> SummarizeChunksAsync(List<string> chunks)
        {
            var summaries = new List<string>();
            for (int i = 0; i < chunks.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var focus = chunks.Count == 1 ? "complete conversation state" : $"chunk {i + 1} of {chunks.Count}";
                summaries.Add(await SummarizeChunkAsync(chunks[i], focus));
            }

            while (summaries.Count > 1)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var combinedChunks = new List<string>();
                var current = new StringBuilder();

                foreach (var summary in summaries)
                {
                    var formatted = summary + "\n\n";
                    if (current.Length + formatted.Length > MaxCompactionChunkChars)
                    {
                        if (current.Length > 0)
                        {
                            combinedChunks.Add(current.ToString());
                            current.Clear();
                        }
                    }

                    current.Append(formatted);
                }

                if (current.Length > 0)
                    combinedChunks.Add(current.ToString());

                if (combinedChunks.Count == 1)
                    return await SummarizeChunkAsync(combinedChunks[0], "merge all chunk summaries into one continuation summary");

                summaries.Clear();
                for (int i = 0; i < combinedChunks.Count; i++)
                    summaries.Add(await SummarizeChunkAsync(combinedChunks[i], $"merge summary chunk {i + 1} of {combinedChunks.Count}"));
            }

            return summaries[0];
        }

        var chunks = BuildCompactionChunks();
        var contentToSummarize = string.Concat(chunks);
        AnsiConsole.MarkupLine($"[dim]Compacting history ({estimatedTokens / 1000:0.0}k tokens, threshold {config.CompactionThreshold / 1000:0.0}k tokens, {chunks.Count:N0} chunk(s))...[/]");

        try
        {
            var summary = await SummarizeChunksAsync(chunks);

            // Store both the raw compacted history and the summary. Do not clear the
            // context store here; recent messages may still reference older entries.
            var storedEntry = ContextStore.Store("chat history pre-compaction", contentToSummarize);
            var summaryEntry = ContextStore.Store("conversation summary (compaction)", summary);

            // Build the new history: system + summary reference + recent messages
            var newHistory = new List<ChatMessage>();

            var compactionNote =
                $"Previous conversation compacted. Continue using this summary as prior context.\n" +
                $"Context store ids: summary={summaryEntry.Id}, raw_history={storedEntry.Id}. Use ctx_read with those ids if details are needed.\n\n" +
                $"Conversation summary:\n{summary}";
            var compactedSystemPrompt = string.IsNullOrWhiteSpace(baseSystemPrompt)
                ? CompactionSystemMarker + compactionNote
                : baseSystemPrompt.TrimEnd() + "\n\n" + CompactionSystemMarker + compactionNote;
            newHistory.Add(new ChatMessage("system", compactedSystemPrompt));

            // Add recent messages
            foreach (var msg in recentMessages)
                newHistory.Add(new ChatMessage(msg.Role, msg.Content));

            history.Clear();
            history.AddRange(newHistory);

            AnsiConsole.MarkupLine($"[dim]Compaction complete. Reduced history to {history.Count} messages.[/]\n");
            return true;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[yellow]Compaction failed ({ex.Message}), continuing with existing history.[/]");
            return false;
        }
    }

    private static async Task WatchEscapeForCancelAsync(CancellationTokenSource cts)
    {
        while (!cts.IsCancellationRequested)
        {
            try
            {
                if (Console.KeyAvailable)
                {
                    var key = Console.ReadKey(intercept: true);
                    if (key.Key == ConsoleKey.Escape)
                    {
                        AnsiConsole.MarkupLine("\n[dim]Cancelling...[/]");
                        cts.Cancel();
                        return;
                    }
                }
            }
            catch (InvalidOperationException)
            {
                return;
            }

            try
            {
                await Task.Delay(25, cts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
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

    private static void ShowChatHelp()
    {
        var table = new Table();
        table.AddColumn("Command");
        table.AddColumn("Description");
        table.AddRow("[bold]/quit[/] or [bold]/exit[/]", "Exit the chat session");
        table.AddRow("[bold]/clear[/]", "Clear conversation history and keep the system prompt");
        table.AddRow("[bold]/sys <prompt>[/]", "Change the system prompt");
        table.AddRow("[bold]/context[/]", "Show chat history and stored context summary");
        table.AddRow("[bold]/context full[/]", "Show full stored message contents sent before the next user message");
        table.AddRow("[bold]/context list[/]", "List stored context entries");
        table.AddRow("[bold]/context search <query>[/]", "Search stored context");
        table.AddRow("[bold]/context read <id> [[query]][/]", "Read a stored entry, optionally focused by query");
        table.AddRow("[bold]/context delete <id>[/]", "Delete a stored context entry");
        table.AddRow("[bold]/context drop <count>[/]", "Remove recent non-system chat messages");
        table.AddRow("[bold]/context clear[/]", "Clear conversation history and keep the system prompt");
        table.AddRow("[bold]/context clear-history[/]", "Clear conversation history and keep the system prompt");
        table.AddRow("[bold]/context clear-store[/]", "Delete stored context entries for this session");
        table.AddRow("[bold]/context clear all[/]", "Clear chat history and stored context");
        table.AddRow("[bold]/vscode[/]", "Show detected VS Code terminal/editor context");
        table.AddRow("[bold]/debug[/]", "Show recent errors (saved to log, survives /clear)");
        table.AddRow("[bold]/debug latest[/]", "Show full details of the most recent error");
        table.AddRow("[bold]/debug list[/]", "Show recent error entries");
        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();
    }

    private static void HandleDebugCommand(string command)
    {
        var rest = command.Length == "/debug".Length ? "" : command["/debug".Length..].Trim();
        if (string.IsNullOrWhiteSpace(rest))
        {
            // Default: show recent errors summary
            AnsiConsole.WriteLine(DebugLog.Recent(5));
            AnsiConsole.MarkupLine($"\n[dim]Log file: {DebugLog.LogPath.EscapeMarkup()}[/]");
            AnsiConsole.MarkupLine("[dim]Use /debug latest for full details of the most recent error.[/]\n");
        }
        else
        {
            var parts = rest.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var subcommand = parts[0].ToLowerInvariant();

            switch (subcommand)
            {
                case "latest":
                    AnsiConsole.WriteLine(DebugLog.Latest());
                    AnsiConsole.MarkupLine($"\n[dim]Log file: {DebugLog.LogPath.EscapeMarkup()}[/]\n");
                    break;
                case "list":
                case "recent":
                    var count = parts.Length > 1 && int.TryParse(parts[1], out var c) ? c : 10;
                    AnsiConsole.WriteLine(DebugLog.Recent(count));
                    AnsiConsole.MarkupLine($"\n[dim]Log file: {DebugLog.LogPath.EscapeMarkup()}[/]\n");
                    break;
                case "path":
                    AnsiConsole.MarkupLine($"[dim]Log file:[/] {DebugLog.LogPath.EscapeMarkup()}\n");
                    break;
                case "help":
                    AnsiConsole.MarkupLine("[bold]/debug[/]          Show recent 5 errors");
                    AnsiConsole.MarkupLine("[bold]/debug latest[/]   Show full details of most recent error");
                    AnsiConsole.MarkupLine("[bold]/debug list [n][/]  Show n recent errors (default 10)");
                    AnsiConsole.MarkupLine("[bold]/debug path[/]      Show log file path\n");
                    break;
                default:
                    AnsiConsole.MarkupLine($"[yellow]Unknown /debug command:[/] {subcommand.EscapeMarkup()}\n");
                    break;
            }
        }
    }

    private static void ShowVscodeContext()
    {
        var table = new Table();
        table.Title("[green]VS Code Context[/]");
        table.AddColumn("Item");
        table.AddColumn("Value");
        table.AddRow("Terminal", VscodeContext.IsRunningInVscodeTerminal() ? "[green]yes[/]" : "[dim]no[/]");
        table.AddRow("File", VscodeContext.GetDisplayValue(VscodeContext.FilePath));
        table.AddRow("Line", VscodeContext.GetDisplayValue(VscodeContext.Line));
        table.AddRow("Column", VscodeContext.GetDisplayValue(VscodeContext.Column));
        table.AddRow("Selection", string.IsNullOrEmpty(VscodeContext.SelectedText) ? "[dim]not provided[/]" : $"{VscodeContext.SelectedText.Length:N0} chars");
        table.AddRow("Context file", VscodeContext.GetDisplayValue(Environment.GetEnvironmentVariable("SIF_VSCODE_CONTEXT_FILE")));
        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine("[dim]Editor context is read from SIF_VSCODE_CONTEXT_FILE or SIF_VSCODE_FILE, SIF_VSCODE_LINE, SIF_VSCODE_COLUMN, SIF_VSCODE_SELECTED_TEXT, and SIF_VSCODE_SELECTED_TEXT_B64.[/]\n");
    }

    private static void HandleContextCommand(string command, List<ChatMessage> history, string[]? tools)
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
                ShowChatHelp();
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

    private static string EstimateContextSize(List<ChatMessage> history)
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

    private static void ClearChatHistory(List<ChatMessage> history)
    {
        var sys = history.FirstOrDefault(m => m.Role == "system");
        history.Clear();
        if (sys != null)
            history.Add(sys);
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
        return new[] { "bash", "read", "edit", "write", "sleep", "serve", "context" };
    }

    private static string BuildInitialSystemPrompt(string? cliSystemPrompt, string[]? tools, IReadOnlyList<SkillFile> skills)
    {
        var basePrompt = !string.IsNullOrWhiteSpace(cliSystemPrompt)
            ? cliSystemPrompt
            : BuildDefaultSystemPrompt(tools);

        return SkillStore.BuildSystemPrompt(basePrompt, skills);
    }

    private static string? BuildDefaultSystemPrompt(string[]? tools)
    {
        if (tools is not { Length: > 0 })
            return null;

        var descriptions = new Dictionary<string, string>
        {
            { "bash", "Run an allowed shell command using Bash on Unix-like systems or PowerShell on Windows" },
            { "read", "Read file contents" },
            { "edit", "Replace exact text in a file" },
            { "write", "Create or overwrite a file" },
            { "sleep", "Pause briefly before continuing or retrying" },
            { "serve", "Start a local static HTTP server for a directory" },
            { "context", "Store and search large tool results out-of-band" },
            { "ctx", "Store and search large tool results out-of-band" },
        };

        var toolList = tools.Select(t => descriptions.TryGetValue(t, out var d) ? d : t)
            .Aggregate((a, b) => $"{a}, {b}");

        return $"You are Sif, a helpful assistant, with access to these tools: {toolList}. " +
               $"Current working directory: {Environment.CurrentDirectory}. " +               
               "\n\nUse tools proactively: " +
               "\n- Use 'bash' with platform-appropriate commands: ls/grep/find on Unix-like systems, or dir/Select-String/Get-ChildItem on Windows" +
               "\n- Use 'read' to view file contents" +
               "\n- Use 'edit' to modify files" +
               "\n- Use 'write' to create new files" +
               "\n- Use 'tool_catalog' to list or enable optional native tools before using less common capabilities" +
               "\n- Use 'sleep' to pause briefly before continuing or retrying" +
               "\n- Use 'serve' to start a local static HTTP server; do not start long-running servers with 'bash'" +
               "\n- Use 'ctx_search' and 'ctx_read' when a tool result says large context was stored" +
               "\n- Use 'ctx_index' for large pasted text or generated data that should be searchable later" +
               "\nThink before acting — explain your plan first." +
               "\nAfter using tools, summarize your key findings in your final answer. Important details from tool results should be restated in natural language so they persist in the conversation history.";
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
        var skills = SkillStore.Load();
        var systemPrompt = BuildInitialSystemPrompt(opts.SystemPrompt, tools, skills);

        AnsiConsole.MarkupLine($"[dim]Model: {config.Model}[/]");
        if (tools?.Length > 0)
            AnsiConsole.MarkupLine($"[dim]Tools: {string.Join(", ", tools)}[/]");
        if (skills.Count > 0)
            AnsiConsole.MarkupLine($"[dim]Skills: {skills.Count}[/]");
        var mcpToolsCount = mcpService.GetTools().Count;
        if (mcpToolsCount > 0)
            AnsiConsole.MarkupLine($"[dim]MCP Tools: {mcpToolsCount}[/]");
        AnsiConsole.MarkupLine($"[dim]Endpoint: {config.BaseUrl}[/]");
        AnsiConsole.Write(new Markup("[dim]Prompt:[/] "));
        AnsiConsole.WriteLine(prompt.Trim());
        AnsiConsole.WriteLine();
        var promptWithEditorContext = VscodeContext.AppendToUserMessage(prompt);

        try
        {
            if (tools?.Length > 0)
            {
                var history = new List<ChatMessage>();
                if (!string.IsNullOrEmpty(systemPrompt))
                    history.Add(new ChatMessage("system", systemPrompt));
                history.Add(new ChatMessage("user", promptWithEditorContext));
                var (response, tokenCount) = await RunWithEscapeCancel(ct => client.ChatWithToolsAsync(history, ct));
                var ctxEstimate = EstimateContextSize(history);
                AnsiConsole.MarkupLine($"\n[dim]({tokenCount} tokens, {ctxEstimate} context)[/]");
            }
            else
            {
                var (response, reasoning) = await client.CompleteAsync(promptWithEditorContext, systemPrompt);
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
        catch (OperationCanceledException)
        {
            AnsiConsole.MarkupLine("[dim]Cancelled.[/]");
            return 130;
        }
        catch (Exception ex)
        {
            var debugPath = DebugLog.Save("complete", ex, prompt?.Trim().Take(100).ToString() ?? "no prompt");
            AnsiConsole.MarkupLine($"[red]Error:[/] {ex.Message.EscapeMarkup()}");
            AnsiConsole.MarkupLine($"[dim]Debug saved to {debugPath.EscapeMarkup()}[/]");
            return 1;
        }

        return 0;
    }

    private static async Task<int> RunSetup(string[] args)
    {
        try
        {
            await RunSetupWizard(firstLaunch: false);
            return 0;
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("non-interactive", StringComparison.OrdinalIgnoreCase))
        {
            AnsiConsole.MarkupLine("[yellow]Error:[/] The setup wizard requires an interactive terminal.");
            AnsiConsole.MarkupLine("[dim]Use `sif config --set KEY=value` to configure sif from a script.[/]");
            return 1;
        }
    }

    private static bool ShouldRunFirstLaunchSetup(CliArgs opts)
    {
        if (AgentConfig.ConfigFileExists)
            return false;

        if (Console.IsInputRedirected)
            return false;

        if (!string.IsNullOrWhiteSpace(opts.BaseUrl) ||
            !string.IsNullOrWhiteSpace(opts.Model) ||
            !string.IsNullOrWhiteSpace(opts.ApiKey) ||
            opts.Tools is { Length: > 0 } ||
            opts.Thinking.HasValue ||
            opts.Temperature.HasValue ||
            opts.MaxTokens.HasValue)
        {
            return false;
        }

        return !HasAgentEnvironmentOverrides();
    }

    private static bool HasAgentEnvironmentOverrides()
    {
        string[] keys =
        [
            "AGENT_BASE_URL",
            "AGENT_API_KEY",
            "AGENT_MODEL",
            "AGENT_TOOLS",
            "AGENT_MAX_TOKENS",
            "AGENT_TEMPERATURE",
            "AGENT_THINKING_ENABLED"
        ];

        return keys.Any(key => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(key)));
    }

    private static async Task RunSetupWizard(bool firstLaunch)
    {
        var config = AgentConfig.Load();

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine(firstLaunch
            ? "[green]Welcome to sif.[/] Let's set up your model connection."
            : "[green]sif setup[/]");
        AnsiConsole.MarkupLine($"[dim]Configuration will be saved to {AgentConfig.ConfigPath.EscapeMarkup()}[/]\n");

        var baseUrl = AnsiConsole.Prompt(
            new TextPrompt<string>("API base URL")
                .DefaultValue(config.BaseUrl)
                .Validate(value =>
                {
                    if (Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) &&
                        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
                    {
                        return ValidationResult.Success();
                    }

                    return ValidationResult.Error("[red]Enter an absolute http or https URL.[/]");
                }));

        var apiKey = AnsiConsole.Prompt(
            new TextPrompt<string>("API key [dim](leave blank for local models)[/]")
                .AllowEmpty()
                .Secret());

        var model = await PromptForModelAsync(baseUrl, apiKey, config.Model);

        var tools = AnsiConsole.Prompt(
            new TextPrompt<string>("Tools")
                .DefaultValue(config.Tools is { Length: > 0 }
                    ? string.Join(",", config.Tools)
                    : "bash,read,edit,write,sleep,serve,context")
                .Validate(value => string.IsNullOrWhiteSpace(value)
                    ? ValidationResult.Error("[red]Enter at least one tool, or rerun with --tools for a custom set.[/]")
                    : ValidationResult.Success()));

        var thinking = AnsiConsole.Confirm("Show model thinking/reasoning when the backend exposes it?", config.ThinkingEnabled ?? true);

        config.BaseUrl = baseUrl.Trim().TrimEnd('/');
        config.Model = model.Trim();
        config.ApiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey;
        config.Tools = tools.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        config.ThinkingEnabled = thinking;

        config.Values["BASE_URL"] = config.BaseUrl;
        config.Values["MODEL"] = config.Model;
        config.Values["TOOLS"] = string.Join(",", config.Tools);
        config.Values["THINKING_ENABLED"] = thinking.ToString().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(config.ApiKey))
            config.Values.Remove("API_KEY");
        else
            config.Values["API_KEY"] = config.ApiKey;

        config.Save();

        AnsiConsole.MarkupLine("\n[green]Configuration saved.[/]");
        AnsiConsole.MarkupLine("[dim]Run `sif config` to review or `sif setup` to change these values later.[/]\n");
    }

    private static async Task<string> PromptForModelAsync(string baseUrl, string apiKey, string currentModel)
    {
        var models = await FetchModelIdsAsync(baseUrl, apiKey);
        if (models.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]Could not fetch models from the endpoint; enter the model name manually.[/]");
            return PromptForManualModel(currentModel);
        }

        const string manualChoice = "Enter manually...";
        var choices = models
            .OrderBy(model => string.Equals(model, currentModel, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(model => model, StringComparer.OrdinalIgnoreCase)
            .ToList();
        choices.Add(manualChoice);

        var selected = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("Model")
                .PageSize(12)
                .AddChoices(choices));

        return selected == manualChoice
            ? PromptForManualModel(currentModel)
            : selected;
    }

    private static string PromptForManualModel(string currentModel)
    {
        return AnsiConsole.Prompt(
            new TextPrompt<string>("Model name")
                .DefaultValue(currentModel)
                .Validate(value => string.IsNullOrWhiteSpace(value)
                    ? ValidationResult.Error("[red]Model name is required.[/]")
                    : ValidationResult.Success()));
    }

    private static async Task<List<string>> FetchModelIdsAsync(string baseUrl, string apiKey)
    {
        using var http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(8)
        };

        if (!string.IsNullOrWhiteSpace(apiKey))
            http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);

        foreach (var url in GetModelEndpointCandidates(baseUrl))
        {
            try
            {
                using var response = await http.GetAsync(url);
                if (!response.IsSuccessStatusCode)
                    continue;

                await using var stream = await response.Content.ReadAsStreamAsync();
                using var doc = await JsonDocument.ParseAsync(stream);
                if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                    continue;

                var models = data.EnumerateArray()
                    .Select(item => item.TryGetProperty("id", out var id) ? id.GetString() : null)
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .Select(id => id!)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (models.Count > 0)
                    return models;
            }
            catch
            {
                // Ignore endpoint probe failures; the wizard falls back to manual entry.
            }
        }

        return new List<string>();
    }

    private static async Task<int?> TryApplyModelCompactionThresholdAsync(AgentConfig config)
    {
        if (config.CompactionThreshold <= 0)
            return null;

        var contextLength = await FetchModelContextLengthAsync(config.BaseUrl, config.ApiKey ?? "", config.Model);
        if (!contextLength.HasValue)
            return null;

        // Treat the built-in value as "auto". Custom configured thresholds stay explicit,
        // even when the custom value happens to equal the built-in default.
        if (!config.CompactionThresholdConfigured)
            config.CompactionThreshold = Math.Max(1000, (int)Math.Floor(contextLength.Value * 0.60));

        return contextLength;
    }

    private static async Task<int?> FetchModelContextLengthAsync(string baseUrl, string apiKey, string modelId)
    {
        using var http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(8)
        };

        if (!string.IsNullOrWhiteSpace(apiKey))
            http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);

        foreach (var url in GetModelEndpointCandidates(baseUrl))
        {
            try
            {
                using var response = await http.GetAsync(url);
                if (!response.IsSuccessStatusCode)
                    continue;

                await using var stream = await response.Content.ReadAsStreamAsync();
                using var doc = await JsonDocument.ParseAsync(stream);
                if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (var item in data.EnumerateArray())
                {
                    if (!item.TryGetProperty("id", out var idElement))
                        continue;

                    var id = idElement.GetString();
                    if (!string.Equals(id, modelId, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var contextLength = TryReadContextLength(item);
                    if (contextLength.HasValue)
                        return contextLength;
                }
            }
            catch
            {
                // Ignore endpoint probe failures; compaction falls back to configured threshold.
            }
        }

        return null;
    }

    private static int? TryReadContextLength(JsonElement model)
    {
        var names = new[]
        {
            "context_length",
            "max_context_length",
            "context_window",
            "max_context_window",
            "max_model_len",
            "max_sequence_length",
            "max_position_embeddings",
            "n_ctx"
        };

        foreach (var name in names)
        {
            if (TryReadIntProperty(model, name, out var value))
                return value;
        }

        if (model.TryGetProperty("metadata", out var metadata) && metadata.ValueKind == JsonValueKind.Object)
        {
            foreach (var name in names)
            {
                if (TryReadIntProperty(metadata, name, out var value))
                    return value;
            }
        }

        return null;
    }

    private static bool TryReadIntProperty(JsonElement element, string name, out int value)
    {
        value = 0;
        if (!element.TryGetProperty(name, out var property))
            return false;

        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out value) && value > 0)
            return true;

        if (property.ValueKind == JsonValueKind.String &&
            int.TryParse(property.GetString(), out value) &&
            value > 0)
            return true;

        value = 0;
        return false;
    }

    private static IEnumerable<string> GetModelEndpointCandidates(string baseUrl)
    {
        var trimmed = baseUrl.Trim().TrimEnd('/');
        if (string.IsNullOrWhiteSpace(trimmed))
            yield break;

        yield return $"{trimmed}/models";

        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            var path = uri.AbsolutePath.TrimEnd('/');
            if (!path.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
            {
                var builder = new UriBuilder(uri)
                {
                    Path = string.IsNullOrWhiteSpace(path) || path == "/"
                        ? "/v1/models"
                        : $"{path}/v1/models",
                    Query = ""
                };
                yield return builder.Uri.ToString();
            }
        }
    }

    private async Task<int> RunConfig(string[] args)
    {
        var config = AgentConfig.Load();

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            string? value = null;

            if (arg.StartsWith("-s=") || arg.StartsWith("--set="))
            {
                value = arg[(arg.StartsWith("-s=") ? 3 : 6)..];
            }
            else if ((arg == "-s" || arg == "--set") && i + 1 < args.Length)
            {
                value = args[++i];
            }

            if (value == null)
                continue;

            var parts = value.Split('=', 2);
            if (parts.Length == 2)
            {
                config.Values[parts[0].Trim().ToUpperInvariant()] = parts[1];
                config.ApplyValue(parts[0], parts[1]);
                config.Save();
                AnsiConsole.MarkupLine("[green]Configuration updated.[/]\n");
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
            ("AGENT_COMPACTION_THRESHOLD", "Compaction threshold", config.CompactionThreshold.ToString()),
            ("AGENT_TOOLS", "Tools", config.Tools is { Length: > 0 } ? string.Join(",", config.Tools) : "default"),
            ("AGENT_SHELL_ALLOWED_COMMANDS", "Shell allowed commands", config.ShellAllowedCommands is { Length: > 0 } ? string.Join(",", config.ShellAllowedCommands) : "default"),
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

    private static async Task<int> RunUninstall(string[] args)
    {
        var force = args.Any(arg => arg is "-y" or "--yes" or "--force");
        var removeConfig = args.Any(arg => arg is "--config" or "--remove-config");

        if (!force)
        {
            AnsiConsole.MarkupLine("[yellow]This will uninstall the sif global tool and remove the companion VS Code extension.[/]");
            if (removeConfig)
                AnsiConsole.MarkupLine($"[yellow]It will also delete {AgentConfig.ConfigPath.EscapeMarkup()}.[/]");

            if (!AnsiConsole.Confirm("Continue?", false))
            {
                AnsiConsole.MarkupLine("[dim]Uninstall cancelled.[/]");
                return 1;
            }
        }

        RemoveVscodeExtension();

        if (removeConfig)
            RemoveConfigFile();

        AnsiConsole.MarkupLine("[dim]Removing .NET global tool package sif.agent...[/]");
        var result = await RunProcessAsync("dotnet", ["tool", "uninstall", "--global", "sif.agent"]);
        if (result.ExitCode == 0)
        {
            if (!string.IsNullOrWhiteSpace(result.Output))
                AnsiConsole.WriteLine(result.Output.Trim());
            AnsiConsole.MarkupLine("[green]sif uninstalled.[/]");
            return 0;
        }

        var combined = string.Join('\n', new[] { result.Output, result.Error }.Where(text => !string.IsNullOrWhiteSpace(text))).Trim();
        AnsiConsole.MarkupLine("[red]Failed to uninstall sif.agent.[/]");
        if (!string.IsNullOrWhiteSpace(combined))
            AnsiConsole.WriteLine(combined);
        return result.ExitCode == 0 ? 1 : result.ExitCode;
    }

    private static void RemoveVscodeExtension()
    {
        const string extensionPrefix = "sif.sif-vscode-";
        var extensionsRoot = Environment.GetEnvironmentVariable("VSCODE_EXTENSIONS");
        if (string.IsNullOrWhiteSpace(extensionsRoot))
            extensionsRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".vscode", "extensions");

        if (!Directory.Exists(extensionsRoot))
            return;

        foreach (var dir in Directory.EnumerateDirectories(extensionsRoot, extensionPrefix + "*"))
        {
            try
            {
                Directory.Delete(dir, recursive: true);
                AnsiConsole.MarkupLine($"[dim]Removed VS Code extension: {dir.EscapeMarkup()}[/]");
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[yellow]Warning:[/] Could not remove VS Code extension {dir.EscapeMarkup()}: {ex.Message.EscapeMarkup()}");
            }
        }
    }

    private static void RemoveConfigFile()
    {
        try
        {
            if (File.Exists(AgentConfig.ConfigPath))
            {
                File.Delete(AgentConfig.ConfigPath);
                AnsiConsole.MarkupLine($"[dim]Removed config: {AgentConfig.ConfigPath.EscapeMarkup()}[/]");
            }
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[yellow]Warning:[/] Could not remove config: {ex.Message.EscapeMarkup()}");
        }
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunProcessAsync(string fileName, string[] arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var argument in arguments)
            psi.ArgumentList.Add(argument);

        using var process = Process.Start(psi);
        if (process == null)
            return (1, "", $"Failed to start {fileName}.");

        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await outputTask, await errorTask);
    }

    private async Task<string?> ReadChatInputAsync(Lazy<List<string>> files, List<string> inputHistory)
    {
        const string PromptText = "> ";
        const string PromptMarkup = "[blue]>[/] ";
        var sb = new StringBuilder();
        int cursor = 0;
        int historyIndex = inputHistory.Count;
        string? historyDraft = null;
        int renderedRowCount = 1;
        int renderedCursorRow = 0;

        void Redraw()
        {
            var width = Math.Max(Console.WindowWidth - 1, 1);
            var indent = PromptText.Length; // visible characters in "> "
            var effectiveWidth = Math.Max(width - indent, 1);
            var text = sb.ToString();
            var lines = text.Split('\n');

            int RowsForLine(string line) => Math.Max((line.Length + effectiveWidth - 1) / effectiveWidth, 1);

            (int Row, int Column) CursorPosition()
            {
                var textUpToCursor = text.Substring(0, Math.Min(cursor, text.Length));
                var cursorLine = textUpToCursor.Count(c => c == '\n');
                var lastNewline = textUpToCursor.LastIndexOf('\n');
                var colOnLine = lastNewline == -1
                    ? textUpToCursor.Length
                    : textUpToCursor.Length - lastNewline - 1;
                var currentLineLength = lines[Math.Min(cursorLine, lines.Length - 1)].Length;

                var row = 0;
                for (int i = 0; i < cursorLine; i++)
                    row += RowsForLine(lines[i]);

                if (colOnLine == 0)
                    return (row, indent);

                if (colOnLine < currentLineLength && colOnLine % effectiveWidth == 0)
                    return (row + (colOnLine / effectiveWidth), indent);

                return (row + ((colOnLine - 1) / effectiveWidth), indent + ((colOnLine - 1) % effectiveWidth) + 1);
            }

            var visibleLines = lines.Sum(RowsForLine);
            var cursorPosition = CursorPosition();

            // Move from the old cursor position back to the top of the previous render.
            for (int i = 0; i < renderedCursorRow; i++)
                Console.Write("\x1b[A");

            // Clear the whole previous render, including rows left behind after deletions.
            Console.Write("\r");
            for (int i = 0; i < renderedRowCount; i++)
            {
                Console.Write("\x1b[2K");
                if (i < renderedRowCount - 1)
                    Console.Write("\x1b[B");
            }

            for (int i = 0; i < renderedRowCount - 1; i++)
                Console.Write("\x1b[A");

            // Re-render from scratch and wrap manually so long input lines have stable rows.
            AnsiConsole.Write(new Markup(PromptMarkup));
            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (line.Length > 0)
                {
                    for (int offset = 0; offset < line.Length; offset += effectiveWidth)
                    {
                        if (offset > 0)
                            Console.Write("\n" + new string(' ', indent));

                        Console.Write(line.Substring(offset, Math.Min(effectiveWidth, line.Length - offset)));
                    }
                }

                if (i < lines.Length - 1) Console.Write("\n" + new string(' ', indent));
            }

            for (int i = 0; i < visibleLines - cursorPosition.Row - 1; i++)
                Console.Write("\x1b[A");

            Console.Write("\r");
            for (int i = 0; i < cursorPosition.Column; i++)
                Console.Write("\x1b[C");

            renderedRowCount = visibleLines;
            renderedCursorRow = cursorPosition.Row;
        }

        void SetInput(string text)
        {
            sb.Clear();
            sb.Append(text);
            cursor = sb.Length;
            Redraw();
        }

        void MoveCursorToRenderedBottom()
        {
            for (int i = 0; i < renderedRowCount - renderedCursorRow - 1; i++)
                Console.Write("\x1b[B");

            Console.Write("\r");
        }

        bool TryReadEscapeSequence(string sequence)
        {
            foreach (var expected in sequence)
            {
                var waited = 0;
                while (!Console.KeyAvailable && waited < 50)
                {
                    Thread.Sleep(2);
                    waited += 2;
                }

                if (!Console.KeyAvailable)
                    return false;

                var next = Console.ReadKey(intercept: true);
                if (next.KeyChar != expected)
                    return false;
            }

            return true;
        }

        async Task<string> ReadBracketedPasteAsync()
        {
            const string endMarker = "\u001b[201~";
            var pasted = new StringBuilder();

            while (true)
            {
                if (!Console.KeyAvailable)
                {
                    await Task.Delay(20);
                    continue;
                }

                var next = Console.ReadKey(intercept: true);
                pasted.Append(next.KeyChar);

                if (pasted.Length >= endMarker.Length &&
                    pasted.ToString().EndsWith(endMarker, StringComparison.Ordinal))
                {
                    pasted.Length -= endMarker.Length;
                    return pasted.ToString().Replace("\r\n", "\n").Replace('\r', '\n');
                }
            }
        }

        Console.Write("\u001b[?2004h");
        try
        {
            AnsiConsole.Write(new Markup(PromptMarkup));

            while (true)
            {
                if (!Console.KeyAvailable)
                {
                    await Task.Delay(20);
                    continue;
                }

                var key = Console.ReadKey(intercept: true);

                if (key.Key == ConsoleKey.Escape && TryReadEscapeSequence("[200~"))
                {
                    var pasted = await ReadBracketedPasteAsync();
                    sb.Insert(cursor, pasted);
                    cursor += pasted.Length;
                    Redraw();
                    continue;
                }

                if (key.Key == ConsoleKey.Enter)
                {
                    if (key.Modifiers.HasFlag(ConsoleModifiers.Alt))
                    {
                        sb.Insert(cursor, '\n');
                        cursor++;
                        Redraw();
                        continue;
                    }
                    MoveCursorToRenderedBottom();
                    Console.WriteLine();
                    return sb.ToString();
                }

                if (key.Key == ConsoleKey.Backspace)
                {
                    if (cursor > 0)
                    {
                        sb.Remove(cursor - 1, 1);
                        cursor--;
                        Redraw();
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

                if (key.Key == ConsoleKey.UpArrow)
                {
                    if (inputHistory.Count > 0 && historyIndex > 0)
                    {
                        if (historyIndex == inputHistory.Count)
                            historyDraft = sb.ToString();

                        historyIndex--;
                        SetInput(inputHistory[historyIndex]);
                    }
                    continue;
                }

                if (key.Key == ConsoleKey.DownArrow)
                {
                    if (historyIndex < inputHistory.Count)
                    {
                        historyIndex++;
                        SetInput(historyIndex == inputHistory.Count ? historyDraft ?? "" : inputHistory[historyIndex]);
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
                    Redraw();
                }
            }
        }
        finally
        {
            Console.Write("\u001b[?2004l");
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

internal static class VscodeContext
{
    private const int MaxInlineTextChars = 6000;
    private const int MaxLineChars = 1000;

    private static string? _lastEditorContext = null;

    public static string? FilePath => ReadSnapshot().FilePath ?? NormalizePath(GetFirstEnv("SIF_VSCODE_FILE", "VSCODE_ACTIVE_FILE"));
    public static string? Line => ReadSnapshot().Line ?? GetFirstEnv("SIF_VSCODE_LINE", "SIF_VSCODE_SELECTION_START_LINE", "VSCODE_ACTIVE_LINE");
    public static string? Column => ReadSnapshot().Column ?? GetFirstEnv("SIF_VSCODE_COLUMN", "SIF_VSCODE_SELECTION_START_COLUMN", "VSCODE_ACTIVE_COLUMN");
    public static string? SelectedText => ReadSnapshot().SelectedText
        ?? DecodeText(GetFirstEnv("SIF_VSCODE_SELECTED_TEXT_B64"), isBase64: true)
        ?? DecodeText(GetFirstEnv("SIF_VSCODE_SELECTED_TEXT", "VSCODE_SELECTED_TEXT"), isBase64: false);

    public static bool IsRunningInVscodeTerminal()
    {
        return string.Equals(Environment.GetEnvironmentVariable("TERM_PROGRAM"), "vscode", StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("VSCODE_IPC_HOOK_CLI"))
            || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("VSCODE_PID"))
            || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SIF_VSCODE_CONTEXT_FILE"));
    }

    public static string AppendToUserMessage(string message)
    {
        if (!IsRunningInVscodeTerminal())
            return message;

        var block = BuildMessageBlock();
        if (string.IsNullOrWhiteSpace(block))
            return message;

        // Only include editor context if it changed since the last turn
        if (block == _lastEditorContext)
        {
            _lastEditorContext = null;
            return message;
        }

        _lastEditorContext = block;
        return block + "\n\nUser message:\n" + message;
    }

    public static string GetDisplayValue(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "[dim]not provided[/]" : value.EscapeMarkup();
    }

    private static string? BuildMessageBlock()
    {
        var snapshot = ReadSnapshot();
        var file = snapshot.FilePath ?? NormalizePath(GetFirstEnv("SIF_VSCODE_FILE", "VSCODE_ACTIVE_FILE"));
        var line = snapshot.Line ?? GetFirstEnv("SIF_VSCODE_LINE", "SIF_VSCODE_SELECTION_START_LINE", "VSCODE_ACTIVE_LINE");
        var column = snapshot.Column ?? GetFirstEnv("SIF_VSCODE_COLUMN", "SIF_VSCODE_SELECTION_START_COLUMN", "VSCODE_ACTIVE_COLUMN");
        var selectedText = snapshot.SelectedText
            ?? DecodeText(GetFirstEnv("SIF_VSCODE_SELECTED_TEXT_B64"), isBase64: true)
            ?? DecodeText(GetFirstEnv("SIF_VSCODE_SELECTED_TEXT", "VSCODE_SELECTED_TEXT"), isBase64: false);
        var currentLine = TryReadLine(file, line);

        if (string.IsNullOrWhiteSpace(file) &&
            string.IsNullOrWhiteSpace(line) &&
            string.IsNullOrWhiteSpace(column) &&
            string.IsNullOrWhiteSpace(selectedText))
            return null;

        var sb = new StringBuilder();
        sb.AppendLine("<editor_context source=\"vscode\">");
        if (!string.IsNullOrWhiteSpace(file))
            sb.AppendLine($"Open file: {file}");
        if (!string.IsNullOrWhiteSpace(line) || !string.IsNullOrWhiteSpace(column))
            sb.AppendLine($"Cursor: line {ValueOrUnknown(line)}, column {ValueOrUnknown(column)}");
        if (!string.IsNullOrWhiteSpace(currentLine))
            sb.AppendLine($"Current line: {currentLine}");
        if (!string.IsNullOrWhiteSpace(selectedText))
        {
            sb.AppendLine("Selected text:");
            sb.AppendLine(TrimText(selectedText, MaxInlineTextChars));
        }
        sb.AppendLine("</editor_context>");
        return sb.ToString().TrimEnd();
    }

    private static string? TryReadLine(string? file, string? line)
    {
        if (string.IsNullOrWhiteSpace(file) || !int.TryParse(line, out var lineNumber) || lineNumber <= 0)
            return null;

        try
        {
            var path = Path.IsPathRooted(file) ? file : Path.GetFullPath(file, Environment.CurrentDirectory);
            if (!File.Exists(path))
                return null;

            var current = 1;
            foreach (var text in File.ReadLines(path))
            {
                if (current == lineNumber)
                    return TrimText(text, MaxLineChars);
                current++;
            }
        }
        catch
        {
            return null;
        }

        return null;
    }

    private static EditorSnapshot ReadSnapshot()
    {
        var path = Environment.GetEnvironmentVariable("SIF_VSCODE_CONTEXT_FILE");
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return EditorSnapshot.Empty;

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            return new EditorSnapshot(
                NormalizePath(GetJsonString(root, "file")),
                GetJsonString(root, "line"),
                GetJsonString(root, "column"),
                GetJsonString(root, "selectedText"));
        }
        catch
        {
            return EditorSnapshot.Empty;
        }
    }

    private static string? GetJsonString(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null
        };
    }

    private static string ValueOrUnknown(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "unknown" : value;
    }

    private static string? GetFirstEnv(params string[] names)
    {
        foreach (var name in names)
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return null;
    }

    private static string? DecodeText(string? value, bool isBase64)
    {
        if (string.IsNullOrEmpty(value))
            return null;

        if (!isBase64)
            return value.Replace("\\r\\n", "\n").Replace("\\n", "\n");

        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(value));
        }
        catch
        {
            return null;
        }
    }

    private static string? NormalizePath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.IsFile)
            return uri.LocalPath;

        return value;
    }

    private static string TrimText(string value, int maxChars)
    {
        if (value.Length <= maxChars)
            return value;

        return value[..maxChars] + $"\n...[truncated {value.Length - maxChars:N0} chars]";
    }

    private sealed record EditorSnapshot(string? FilePath, string? Line, string? Column, string? SelectedText)
    {
        public static EditorSnapshot Empty { get; } = new(null, null, null, null);
    }
}
