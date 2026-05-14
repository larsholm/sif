using System.ClientModel;
#pragma warning disable OPENAI001
using System.Text;
using System.Text.Json;
using OpenAI;
using Spectre.Console;

namespace sif.agent;

/// <summary>
/// Client wrapping the OpenAI SDK for chat completions.
/// Supports lazy tool calling for native tools and MCP tools.
/// </summary>
internal class AgentClient
{
    private readonly OpenAI.Chat.ChatClient _chatClient;
    private readonly HashSet<string> _availableLocalTools = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _activeLocalTools = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<OpenAI.Chat.ChatTool> _mcpTools = new();
    private readonly McpService? _mcpService;
    private readonly bool _thinkingEnabled;
    private readonly string _modelName;
    private readonly bool _isOModel;
    private readonly float? _temperature;
    private readonly int? _maxTokens;

    public AgentClient(AgentConfig config, string[]? enabledTools = null, McpService? mcpService = null)
    {
        var endpoint = config.BaseUrl.TrimEnd('/');
        var apiKey = string.IsNullOrEmpty(config.ApiKey) ? "none" : config.ApiKey;

        OpenAIClient openAIClient;

        if (!endpoint.Contains("openai.com", StringComparison.OrdinalIgnoreCase))
        {
            openAIClient = new OpenAIClient(new ApiKeyCredential(apiKey), new OpenAI.OpenAIClientOptions
            {
                Endpoint = new Uri(endpoint)
            });
        }
        else
        {
            openAIClient = new OpenAIClient(apiKey);
        }

        _chatClient = openAIClient.GetChatClient(config.Model);
        _modelName = config.Model;
        _thinkingEnabled = config.ThinkingEnabled ?? false;
        _isOModel = _modelName.StartsWith("o1", StringComparison.OrdinalIgnoreCase) ||
                    _modelName.StartsWith("o3", StringComparison.OrdinalIgnoreCase);
        _mcpService = mcpService;
        _temperature = config.Temperature;
        _maxTokens = config.MaxTokens;

        if (enabledTools?.Length > 0)
        {
            foreach (var tool in ExpandToolNames(enabledTools))
                _availableLocalTools.Add(tool);

            foreach (var tool in SelectInitialTools(_availableLocalTools))
                _activeLocalTools.Add(tool);
        }
        
        if (_mcpService != null)
            _mcpTools.AddRange(_mcpService.GetTools());
    }

    private void ApplyThinkingOptions(OpenAI.Chat.ChatCompletionOptions opts)
    {
        if (_temperature.HasValue) opts.Temperature = _temperature;
        if (_maxTokens.HasValue) opts.MaxOutputTokenCount = _maxTokens;

        if (!_thinkingEnabled) return;

        // Only OpenAI o-series models support ReasoningEffortLevel via the SDK.
        // Qwen3.x has thinking enabled by default — no request parameter needed.
        // For other OpenAI-compatible endpoints, we don't send thinking parameters
        // because they may not be supported and can cause API errors.
        if (_isOModel)
        {
            opts.ReasoningEffortLevel = OpenAI.Chat.ChatReasoningEffortLevel.High;
        }
    }

    /// <summary>
    /// Extract the "reasoning" field from a raw API response.
    /// vLLM / Qwen return reasoning as a separate field on the choice message.
    /// </summary>
    private static string ExtractReasoningFromRawResponse(ClientResult<OpenAI.Chat.ChatCompletion> result)
    {
        try
        {
            var rawResponse = result.GetRawResponse();
            var json = rawResponse.Content.ToString();
            using var doc = JsonDocument.Parse(json);
            var choices = doc.RootElement.GetProperty("choices");
            if (choices.GetArrayLength() > 0)
            {
                var choice = choices[0];
                var message = choice.GetProperty("message");
                if (message.TryGetProperty("reasoning", out var reasoning))
                {
                    var text = reasoning.GetString();
                    if (!string.IsNullOrEmpty(text))
                        return text.Trim();
                }
            }
        }
        catch
        {
            // Parsing failed — no reasoning available
        }
        return "";
    }

    /// <summary>
    /// Generate a focused summary of arbitrary content using the LLM.
    /// Capped at 4000 characters.
    /// </summary>
    public async Task<string> SummarizeAsync(string content, string focus)
    {
        var systemPrompt = $"Summarize the following content, focusing on {focus}. Be concise but thorough. Limit your response to 4000 characters.";
        var prompt = content.Length > 80000 ? content[..80000] : content;

        var (response, _) = await CompleteAsync(prompt, systemPrompt);
        return response.Length > 4000 ? response[..4000] : response;
    }

    /// <summary>
    /// Send a single prompt (no conversation history, no tools).
    /// Returns (responseText, reasoningText).
    /// </summary>
    public async Task<(string Response, string Reasoning)> CompleteAsync(string prompt, string? systemPrompt = null)
    {
        var messages = new List<OpenAI.Chat.ChatMessage>();

        if (!string.IsNullOrEmpty(systemPrompt))
            messages.Add(OpenAI.Chat.ChatMessage.CreateSystemMessage(systemPrompt));

        messages.Add(OpenAI.Chat.ChatMessage.CreateUserMessage(prompt));

        var opts = new OpenAI.Chat.ChatCompletionOptions();
        ApplyThinkingOptions(opts);
        var result = await _chatClient.CompleteChatAsync(messages, opts);

        string reasoningText = ExtractReasoningFromRawResponse(result);
        var contentText = ExtractText(result.Value.Content);

        if (string.IsNullOrEmpty(reasoningText))
            reasoningText = ExtractThinking(contentText);

        return (StripThinkingTags(contentText), reasoningText);
    }

    /// <summary>
    /// Chat with tool calling support. Loops through tool calls until the model
    /// returns a text response. Returns (responseText, totalTokenCount).
    /// </summary>
    public async Task<(string Response, int TokenCount)> ChatWithToolsAsync(List<ChatMessage> history, CancellationToken cancellationToken = default)
    {
        var messages = history.Select(m => ToRequestMessage(m)).ToList();
        int totalTokens = 0;

        while (true)
            {
                AnsiConsole.Write(new Markup("[dim]Thinking...[/]"));
                var opts = new OpenAI.Chat.ChatCompletionOptions();
                foreach (var tool in GetCurrentTools())
                    opts.Tools.Add(tool);
                ApplyThinkingOptions(opts);

                var result = await _chatClient.CompleteChatAsync(messages, opts, cancellationToken);

                totalTokens += result.Value.Usage?.TotalTokenCount ?? 0;

                // Extract reasoning from the raw response (vLLM/Qwen) or from content tags
                string reasoningText = ExtractReasoningFromRawResponse(result);
                var contentText = ExtractText(result.Value.Content);

                // Fall back to extracting thinking tags from content if no separate reasoning field
                if (string.IsNullOrEmpty(reasoningText))
                    reasoningText = ExtractThinking(contentText);

                // Show reasoning/thinking before tool calls or final response
                if (!string.IsNullOrEmpty(reasoningText))
                {
                    AnsiConsole.MarkupLine("\n[dim]Thinking:[/]");
                    foreach (var line in reasoningText.Trim().Split('\n'))
                        AnsiConsole.MarkupLine("[dim]  " + line.EscapeMarkup() + "[/]");
                    AnsiConsole.MarkupLine("");
                }

                // Check if the model wants to call tools
                if (result.Value.ToolCalls.Count > 0)
                {
                    messages.Add(OpenAI.Chat.ChatMessage.CreateAssistantMessage(result.Value));

                    foreach (var toolCall in result.Value.ToolCalls)
                    {
                        var toolName = toolCall.FunctionName;
                        var argsJson = toolCall.FunctionArguments.ToString();

                        var preview = argsJson.Length > 80 ? argsJson.Substring(0, 80) + "..." : argsJson;
                        AnsiConsole.MarkupLine($"\n[dim]Tool: {toolName.EscapeMarkup()} ({preview.EscapeMarkup()})[/]");

                        string toolResult;
                        try
                        {
                            if (toolName == "tool_catalog")
                            {
                                toolResult = RunToolCatalog(argsJson);
                            }
                            else if (toolName == "ctx_summarize")
                            {
                                toolResult = await RunContextSummarize(argsJson);
                            }
                            else if (IsLocalTool(toolName))
                            {
                                toolResult = await ToolRegistry.ExecuteAsync(toolName, argsJson, cancellationToken);
                            }
                            else if (_mcpService != null)
                            {
                                toolResult = await _mcpService.ExecuteToolAsync(toolName, argsJson);
                            }
                            else
                            {
                                toolResult = $"Error: Tool '{toolName}' not found.";
                            }
                        }
                        catch (Exception ex)
                        {
                            var msgChars = messages.Sum(m => m.GetType().GetProperty("Content")?.GetValue(m)?.ToString()?.Length ?? 0);
                            var ctxEntries = ContextStore.ListEntries();
                            var storedChars = ctxEntries.Sum(e => e.Length);
                            var contextInfo = $"messages: ~{msgChars / 4:N0} tokens ({msgChars:N0} chars), stored: {ctxEntries.Count:N0} entries ({storedChars:N0} chars)";

                            var debugPath = DebugLog.Save(
                                $"tool:{toolName}", ex,
                                $"args: {preview}\n{contextInfo}");

                            toolResult = $"Error in tool '{toolName}': {ex.Message}";
                            toolResult += $"\n\nFull debug info saved to: {debugPath}";
                            AnsiConsole.MarkupLine($"[red]Exception in {toolName}:[/] {ex.Message.EscapeMarkup()}");
                            AnsiConsole.MarkupLine($"[dim]Debug saved to {debugPath.EscapeMarkup()}[/]");
                        }

                        if (!IsContextTool(toolName) && toolResult.Length > ContextStore.AutoStoreThreshold)
                        {
                            var originalResult = toolResult;
                            toolResult = ContextStore.StoreAndDescribe($"{toolName} {preview}", toolResult);
                            // Also generate a summary automatically to help the LLM understand the stored content
                            AnsiConsole.MarkupLine("[dim]Summarizing stored content...[/]");
                            var summary = await SummarizeAsync(originalResult, "the most important facts, values, and structure");
                            toolResult += $"\n\nSummary:\n{summary}";
                        }

                        // Display tool result
                        if (toolResult.Length > 8000)
                            AnsiConsole.MarkupLine($"[dim]Result: {toolResult.Substring(0, 8000).EscapeMarkup()}... (truncated)[/]");
                        else
                            AnsiConsole.MarkupLine($"[dim]Result: {toolResult.EscapeMarkup()}[/]");

                        // Truncate long results for the model
                        if (toolResult.Length > 120000)
                            toolResult = toolResult.Substring(0, 120000) + "\n... (truncated)";

                        messages.Add(OpenAI.Chat.ChatMessage.CreateToolMessage(toolCall.Id, toolResult));

                        // Persist tool calls and results to history so the model can see
                        // what happened on previous turns
                        var toolCallContent = $"Called tool: {toolName} with arguments: {argsJson}";
                        history.Add(new ChatMessage("assistant", toolCallContent));
                        history.Add(new ChatMessage("tool", $"Result from {toolName}:\n{toolResult}", toolCall.Id));
                    }

                    // Continue the loop to get the next response
                    continue;
                }

                // Final text response — strip thinking tags if they're in the content
                var cleanContent = StripThinkingTags(contentText);

                await UiService.DisplayMarkdown(cleanContent);
                AnsiConsole.WriteLine();
                
                history.Add(new ChatMessage("assistant", cleanContent));
                messages.Add(OpenAI.Chat.ChatMessage.CreateAssistantMessage(result.Value));

                return (cleanContent, totalTokens);
            }
    }

    /// <summary>
    /// Send a full conversation and get a complete response (no tools).
    /// Returns (responseText, reasoningText) where reasoning is displayed separately.
    /// </summary>
    public async Task<(string Response, string Reasoning)> ChatAsync(List<ChatMessage> history, CancellationToken cancellationToken = default)
    {
        var messages = history.Select(m => ToRequestMessage(m)).ToList();
        var opts = new OpenAI.Chat.ChatCompletionOptions();
        ApplyThinkingOptions(opts);
        var result = await _chatClient.CompleteChatAsync(messages, opts, cancellationToken);

        string reasoningText = ExtractReasoningFromRawResponse(result);
        var contentText = ExtractText(result.Value.Content);

        // Fall back to extracting thinking tags from content
        if (string.IsNullOrEmpty(reasoningText))
            reasoningText = ExtractThinking(contentText);

        return (StripThinkingTags(contentText), reasoningText);
    }

    /// <summary>
    /// Send a full conversation with streaming output.
    /// Returns the full response text and total token count.
    /// Note: reasoning/thinking is only available in non-streaming mode.
    /// </summary>
    public async Task<(string Response, int TokenCount)> ChatStreamingAsync(List<ChatMessage> history, CancellationToken cancellationToken = default)
    {
        var messages = history.Select(m => ToRequestMessage(m)).ToList();
        var opts = new OpenAI.Chat.ChatCompletionOptions();
        ApplyThinkingOptions(opts);
        var stream = _chatClient.CompleteChatStreamingAsync(messages, opts, cancellationToken);

        var sb = new StringBuilder();
        int totalTokens = 0;

        await foreach (var update in stream.WithCancellation(cancellationToken))
        {
            if (update.ContentUpdate is not null)
            {
                var text = ExtractText(update.ContentUpdate);
                if (text.Length > 0)
                {
                    sb.Append(text);
                    AnsiConsole.Markup(text.EscapeMarkup());
                }
            }

            if (update.Usage is { } usage)
            {
                totalTokens = usage.TotalTokenCount;
            }
        }

        return (sb.ToString(), totalTokens);
    }

    private List<OpenAI.Chat.ChatTool> GetCurrentTools()
    {
        var visible = new HashSet<string>(_activeLocalTools, StringComparer.OrdinalIgnoreCase);
        if (_availableLocalTools.Except(_activeLocalTools, StringComparer.OrdinalIgnoreCase).Any())
            visible.Add("tool_catalog");

        var tools = ToolRegistry.GetTools(visible.ToArray());
        tools.AddRange(_mcpTools);
        return tools;
    }

    private string RunToolCatalog(string argsJson)
    {
        var enabled = new List<string>();
        using var doc = JsonDocument.Parse(argsJson);
        if (doc.RootElement.TryGetProperty("enable", out var enable) && enable.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in enable.EnumerateArray())
            {
                var name = item.GetString();
                if (string.IsNullOrWhiteSpace(name))
                    continue;

                if (_availableLocalTools.Contains(name))
                {
                    _activeLocalTools.Add(name);
                    enabled.Add(name);
                }
            }
        }

        var optional = _availableLocalTools
            .Except(_activeLocalTools, StringComparer.OrdinalIgnoreCase)
            .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
            .Select(t => $"{t}: {DescribeTool(t)}");

        var sb = new StringBuilder();
        if (enabled.Count > 0)
            sb.AppendLine("Enabled: " + string.Join(", ", enabled));
        sb.AppendLine("Active: " + string.Join(", ", _activeLocalTools.OrderBy(t => t, StringComparer.OrdinalIgnoreCase)));
        sb.AppendLine("Optional tools:");
        var optionalText = string.Join('\n', optional);
        sb.AppendLine(string.IsNullOrWhiteSpace(optionalText) ? "(none)" : optionalText);
        return sb.ToString().TrimEnd();
    }

    private async Task<string> RunContextSummarize(string argsJson)
    {
        using var doc = JsonDocument.Parse(argsJson);
        var root = doc.RootElement;
        var id = root.GetProperty("id").GetString() ?? "";
        var focus = root.TryGetProperty("focus", out var f) && f.ValueKind == JsonValueKind.String ? f.GetString() : null;

        if (string.IsNullOrEmpty(id))
            return "Error: id is required.";

        var entry = ContextStore.ListEntries().FirstOrDefault(e => e.Id == id);
        if (entry == null)
            return $"Error: context id not found: {id}";
        if (!File.Exists(entry.Path))
            return $"Error: context blob missing for {id}.";

        var content = File.ReadAllText(entry.Path);
        var defaultFocus = string.IsNullOrEmpty(focus) ? "the most important information" : focus;

        AnsiConsole.MarkupLine($"[dim]Summarizing {id} (focus: {defaultFocus})...[/]");
        var summary = await SummarizeAsync(content, defaultFocus);

        return $"Summary of [{entry.Id}] {entry.Source} (focus: {defaultFocus}):\n\n{summary}";
    }

    private static IEnumerable<string> ExpandToolNames(IEnumerable<string> tools)
    {
        foreach (var tool in tools.Select(t => t.Trim()).Where(t => t.Length > 0))
        {
            if (tool.Equals("context", StringComparison.OrdinalIgnoreCase) ||
                tool.Equals("ctx", StringComparison.OrdinalIgnoreCase))
            {
                yield return "ctx_index";
                yield return "ctx_search";
                yield return "ctx_read";
                yield return "ctx_summarize";
                yield return "ctx_stats";
            }
            else
            {
                yield return tool;
            }
        }
    }

    private static IEnumerable<string> SelectInitialTools(HashSet<string> available)
    {
        var initial = new[] { "bash", "read", "edit", "write", "ctx_search", "ctx_read", "ctx_summarize", "roslyn" };
        foreach (var tool in initial)
        {
            if (available.Contains(tool))
                yield return tool;
        }
    }

    private static string DescribeTool(string toolName)
    {
        return toolName switch
        {
            "sleep" => "pause briefly before retrying",
            "serve" => "start a local static HTTP server",
            "ctx_index" => "store large generated/pasted text",
            "ctx_summarize" => "summarize stored context with focus",
            "ctx_stats" => "show context-store stats",
            _ => "native tool"
        };
    }

    private static bool IsLocalTool(string toolName)
    {
        return toolName is "bash" or "read" or "edit" or "write" or "sleep" or "serve" or "tool_catalog"
            or "ctx_index" or "ctx_search" or "ctx_read" or "ctx_summarize" or "ctx_stats"
            or "roslyn_find_symbols" or "roslyn_get_diagnostics";
    }

    private static bool IsContextTool(string toolName)
    {
        return toolName is "ctx_index" or "ctx_search" or "ctx_read" or "ctx_summarize" or "ctx_stats";
    }

    private static OpenAI.Chat.ChatMessage ToRequestMessage(ChatMessage msg)
    {
        return msg.Role switch
        {
            "system" => OpenAI.Chat.ChatMessage.CreateSystemMessage(msg.Content),
            "assistant" => OpenAI.Chat.ChatMessage.CreateAssistantMessage(msg.Content),
            "tool" => OpenAI.Chat.ChatMessage.CreateToolMessage(msg.ToolCallId ?? "unknown", msg.Content),
            _ => OpenAI.Chat.ChatMessage.CreateUserMessage(msg.Content),
        };
    }

    private static string ExtractText(OpenAI.Chat.ChatMessageContent content)
    {
        var sb = new StringBuilder();
        foreach (var part in content)
        {
            if (part.Text is { Length: > 0 } text)
                sb.Append(text);
        }
        return sb.ToString();
    }

    private static string StripThinkingTags(string text)
    {
        // Strip <thinking>...</thinking>, <thought>...</thought>, etc.
        text = System.Text.RegularExpressions.Regex.Replace(text, @"<\/?(?:thinking|thought|reasoning|think)>\s*", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return text;
    }

    private static string ExtractThinking(string text)
    {
        // Extract content from <thinking>...</thinking>, <thought>...</thought>, etc.
        var match = System.Text.RegularExpressions.Regex.Match(text, @"<thinking>(.*?)</thinking>", System.Text.RegularExpressions.RegexOptions.Singleline | System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (match.Success) return match.Groups[1].Value;
        match = System.Text.RegularExpressions.Regex.Match(text, @"<thought>(.*?)</thought>", System.Text.RegularExpressions.RegexOptions.Singleline | System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (match.Success) return match.Groups[1].Value;
        match = System.Text.RegularExpressions.Regex.Match(text, @"<reasoning>(.*?)</reasoning>", System.Text.RegularExpressions.RegexOptions.Singleline | System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (match.Success) return match.Groups[1].Value;
        return "";
    }
}
