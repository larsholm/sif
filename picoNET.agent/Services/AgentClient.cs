using System.ClientModel;
#pragma warning disable OPENAI001
using System.Text;
using System.Text.Json;
using OpenAI;
using Spectre.Console;
using ConsoleMarkdownRenderer;

namespace picoNET.agent;

/// <summary>
/// Client wrapping the OpenAI SDK for chat completions.
/// Supports tool calling for bash, read, edit, write, sleep, context, and diagnostics.
/// </summary>
internal class AgentClient
{
    private readonly OpenAI.Chat.ChatClient _chatClient;
    private readonly List<OpenAI.Chat.ChatTool>? _tools;
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

        var allTools = new List<OpenAI.Chat.ChatTool>();
        if (enabledTools?.Length > 0)
            allTools.AddRange(ToolRegistry.GetTools(enabledTools));
        
        if (_mcpService != null)
            allTools.AddRange(_mcpService.GetTools());

        if (allTools.Count > 0)
            _tools = allTools;
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
    public async Task<(string Response, int TokenCount)> ChatWithToolsAsync(List<ChatMessage> history)
    {
        var messages = history.Select(m => ToRequestMessage(m)).ToList();
        int totalTokens = 0;

        // Register diagnostics handler
        ToolRegistry.DiagnosticsHandler = async (item) =>
        {
            if (item == "history")
            {
                var json = JsonSerializer.Serialize(messages, new JsonSerializerOptions { WriteIndented = true });
                return $"Current conversation history (OpenAI Format):\n\n{json}";
            }
            return $"Error: Unknown diagnostics item '{item}'";
        };

        try
        {
            while (true)
            {
                AnsiConsole.Write(new Markup("[dim]Thinking...[/]"));
                var opts = new OpenAI.Chat.ChatCompletionOptions();
                foreach (var tool in _tools ?? Enumerable.Empty<OpenAI.Chat.ChatTool>())
                    opts.Tools.Add(tool);
                ApplyThinkingOptions(opts);

                var result = await _chatClient.CompleteChatAsync(messages, opts);

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
                        if (IsLocalTool(toolName))
                        {
                            toolResult = await ToolRegistry.ExecuteAsync(toolName, argsJson);
                        }
                        else if (_mcpService != null)
                        {
                            toolResult = await _mcpService.ExecuteToolAsync(toolName, argsJson);
                        }
                        else
                        {
                            toolResult = $"Error: Tool '{toolName}' not found.";
                        }

                        if (!IsContextTool(toolName) && toolResult.Length > ContextStore.AutoStoreThreshold)
                        {
                            toolResult = ContextStore.StoreAndDescribe($"{toolName} {preview}", toolResult);
                        }

                        // Display tool result
                        if (toolResult.Length > 4000)
                            AnsiConsole.MarkupLine($"[dim]Result: {toolResult.Substring(0, 4000).EscapeMarkup()}... (truncated)[/]");
                        else
                            AnsiConsole.MarkupLine($"[dim]Result: {toolResult.EscapeMarkup()}[/]");

                        // Truncate long results for the model
                        if (toolResult.Length > 16000)
                            toolResult = toolResult.Substring(0, 16000) + "\n... (truncated)";

                        messages.Add(OpenAI.Chat.ChatMessage.CreateToolMessage(toolCall.Id, toolResult));
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
        finally
        {
            ToolRegistry.DiagnosticsHandler = null;
        }
    }

    /// <summary>
    /// Send a full conversation and get a complete response (no tools).
    /// Returns (responseText, reasoningText) where reasoning is displayed separately.
    /// </summary>
    public async Task<(string Response, string Reasoning)> ChatAsync(List<ChatMessage> history)
    {
        var messages = history.Select(m => ToRequestMessage(m)).ToList();
        var opts = new OpenAI.Chat.ChatCompletionOptions();
        ApplyThinkingOptions(opts);
        var result = await _chatClient.CompleteChatAsync(messages, opts);

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
    public async Task<(string Response, int TokenCount)> ChatStreamingAsync(List<ChatMessage> history)
    {
        var messages = history.Select(m => ToRequestMessage(m)).ToList();
        var opts = new OpenAI.Chat.ChatCompletionOptions();
        ApplyThinkingOptions(opts);
        var stream = _chatClient.CompleteChatStreamingAsync(messages, opts);

        var sb = new StringBuilder();
        int totalTokens = 0;

        await foreach (var update in stream)
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

    private static bool IsLocalTool(string toolName)
    {
        return toolName is "bash" or "read" or "edit" or "write" or "sleep" or "debug" or "diagnostics"
            or "ctx_index" or "ctx_search" or "ctx_read" or "ctx_stats";
    }

    private static bool IsContextTool(string toolName)
    {
        return toolName is "ctx_index" or "ctx_search" or "ctx_read" or "ctx_stats";
    }

    private static OpenAI.Chat.ChatMessage ToRequestMessage(ChatMessage msg)
    {
        return msg.Role switch
        {
            "system" => OpenAI.Chat.ChatMessage.CreateSystemMessage(msg.Content),
            "assistant" => OpenAI.Chat.ChatMessage.CreateAssistantMessage(msg.Content),
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
