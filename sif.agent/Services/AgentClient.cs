using System.ClientModel;
using System.ClientModel.Primitives;
#pragma warning disable OPENAI001
using System.Text;
using System.Text.Json;
using OpenAI;
using Spectre.Console;
using sif.agent.Services;

namespace sif.agent;

/// <summary>
/// Client wrapping the OpenAI SDK for chat completions.
/// Supports lazy tool calling for native tools and MCP tools.
/// </summary>
internal class AgentClient
{
    private const int MaxTransientModelRetries = 2;
    private const string ToolParseRetryInstruction =
        "The previous completion was rejected because the tool call was malformed. Retry once. " +
        "If you need a tool, call the provided function directly with a valid JSON object for arguments. " +
        "Do not emit XML, <tool_call>, <function>, or <parameter> tags.";
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

    public ModelRequestSnapshot? LastRequestSnapshot { get; private set; }

    public void ClearLastRequestSnapshot() => LastRequestSnapshot = null;

    public AgentClient(AgentConfig config, string[]? enabledTools = null, McpService? mcpService = null)
    {
        var endpoint = config.BaseUrl.TrimEnd('/');
        var apiKey = string.IsNullOrEmpty(config.ApiKey) ? "" : config.ApiKey;

        OpenAIClient openAIClient;
        var clientOptions = new OpenAI.OpenAIClientOptions();
        clientOptions.AddPolicy(new OpenAICompatibleErrorPolicy(), PipelinePosition.PerCall);
        if (config.ModelTimeoutSeconds is > 0)
            clientOptions.NetworkTimeout = TimeSpan.FromSeconds(config.ModelTimeoutSeconds.Value);

        if (!endpoint.Contains("openai.com", StringComparison.OrdinalIgnoreCase))
        {
            var hasApiKey = !string.IsNullOrWhiteSpace(apiKey);
            var localApiKey = hasApiKey ? apiKey : "not-needed";
            if (!hasApiKey)
                clientOptions.AddPolicy(new RemoveAuthorizationPolicy(), PipelinePosition.BeforeTransport);
            clientOptions.Endpoint = new Uri(endpoint);
            openAIClient = new OpenAIClient(new ApiKeyCredential(localApiKey), clientOptions);
        }
        else
        {
            openAIClient = new OpenAIClient(new ApiKeyCredential(apiKey), clientOptions);
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
    public async Task<(string Response, string Reasoning)> CompleteAsync(
        string prompt,
        string? systemPrompt = null,
        CancellationToken cancellationToken = default)
    {
        var messages = new List<OpenAI.Chat.ChatMessage>();

        if (!string.IsNullOrEmpty(systemPrompt))
            messages.Add(OpenAI.Chat.ChatMessage.CreateSystemMessage(systemPrompt));

        messages.Add(OpenAI.Chat.ChatMessage.CreateUserMessage(prompt));

        var opts = new OpenAI.Chat.ChatCompletionOptions();
        ApplyThinkingOptions(opts);
        var result = await CompleteChatWithRecoveryAsync(messages, opts, cancellationToken);

        if (!ChatResponseParsing.HasChoices(result))
            throw new InvalidOperationException("The model server returned an empty response (no choices).");

        string reasoningText = ChatResponseParsing.ExtractReasoningFromRawResponse(result);
        var contentText = ExtractText(result.Value.Content);

        if (string.IsNullOrEmpty(reasoningText))
            reasoningText = ChatResponseParsing.ExtractThinking(contentText);

        return (ChatResponseParsing.StripThinkingTags(contentText), reasoningText);
    }

    /// <summary>
    /// Chat with tool calling support. Loops through tool calls until the model
    /// returns a text response. Returns (responseText, totalTokenCount).
    /// </summary>
    public async Task<(string Response, int TokenCount)> ChatWithToolsAsync(
        List<ChatMessage> history,
        CancellationToken cancellationToken = default,
        Func<IReadOnlyList<string>>? takeSteeringComments = null,
        Action? onHistoryChanged = null,
        bool streaming = false)
    {
        var messages = history.Select(m => ToRequestMessage(m)).ToList();
        var requestMessages = history.Select(ToRequestSnapshotMessage).ToList();
        int totalTokens = 0;
        int totalOutputTokens = 0;
        var totalTime = TimeSpan.Zero;
        var toolTime = TimeSpan.Zero;
        int modelCalls = 0;
        int toolCallCount = 0;
        var turnSw = System.Diagnostics.Stopwatch.StartNew();
        var malformedToolCallRetries = 0;
        var emptyResponseRetries = 0;

        while (true)
            {
                AnsiConsole.Write(new Markup("[dim]Thinking...[/]"));
                var opts = new OpenAI.Chat.ChatCompletionOptions();
                foreach (var tool in GetCurrentTools())
                    opts.Tools.Add(tool);
                ApplyThinkingOptions(opts);
                CaptureRequest(requestMessages, opts.Tools, streaming);

                var sw = System.Diagnostics.Stopwatch.StartNew();
                ClientResult<OpenAI.Chat.ChatCompletion>? result = null;
                StreamedChatCompletion? streamedResult = null;
                try
                {
                    if (streaming)
                        streamedResult = await CompleteChatStreamingWithRecoveryAsync(messages, opts, cancellationToken);
                    else
                        result = await CompleteChatWithRecoveryAsync(messages, opts, cancellationToken);
                }
                catch (ClientResultException ex) when (malformedToolCallRetries == 0 && ChatResponseParsing.IsProviderToolParseError(ex))
                {
                    malformedToolCallRetries++;
                    sw.Stop();
                    AnsiConsole.MarkupLine("\n[yellow]Provider rejected a malformed tool call; retrying once with stricter tool-call instructions.[/]");
                    messages.Add(OpenAI.Chat.ChatMessage.CreateSystemMessage(ToolParseRetryInstruction));
                    requestMessages.Add(new ModelRequestMessage("system", ToolParseRetryInstruction));
                    continue;
                }
                sw.Stop();

                var inputAndOutputTokens = streamedResult?.TotalTokenCount ?? result!.Value.Usage?.TotalTokenCount ?? 0;
                var generatedTokens = streamedResult?.OutputTokenCount ?? result!.Value.Usage?.OutputTokenCount ?? 0;
                totalTokens += inputAndOutputTokens;
                totalOutputTokens += generatedTokens;
                totalTime += sw.Elapsed;
                modelCalls++;

                // Guard against a degenerate completion with an empty `choices` array.
                // The SDK's flattened accessors (.Content, .ToolCalls, …) dereference
                // Choices[0] and would throw ArgumentOutOfRangeException. Retry once for
                // a transient empty response, then surface a clear error.
                if (result is not null && !ChatResponseParsing.HasChoices(result))
                {
                    if (emptyResponseRetries == 0)
                    {
                        emptyResponseRetries++;
                        AnsiConsole.MarkupLine("\n[yellow]Model returned an empty response (no choices); retrying once.[/]");
                        continue;
                    }
                    throw new InvalidOperationException(
                        "The model server returned an empty response (no choices) after a retry. " +
                        "This usually means the local model failed to generate output; try again or check the server logs.");
                }

                // Extract reasoning from the raw response (vLLM/Qwen) or from content tags
                string reasoningText = result is null ? "" : ChatResponseParsing.ExtractReasoningFromRawResponse(result);
                var contentText = streamedResult?.Content ?? ExtractText(result!.Value.Content);
                var finishReason = streamedResult?.FinishReason ?? ChatResponseParsing.ExtractFinishReason(result!);

                if (string.Equals(finishReason, "length", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        "The model response was truncated because it reached the available context or output-token limit. " +
                        "Increase the model context size or reduce the conversation before continuing.");
                }

                // Fall back to extracting thinking tags from content if no separate reasoning field
                if (string.IsNullOrEmpty(reasoningText))
                    reasoningText = ChatResponseParsing.ExtractThinking(contentText);

                // Show reasoning/thinking before tool calls or final response
                if (!string.IsNullOrEmpty(reasoningText))
                {
                    AnsiConsole.MarkupLine("\n[dim]Thinking:[/]");
                    foreach (var line in reasoningText.Trim().Split('\n'))
                        AnsiConsole.MarkupLine("[dim]  " + line.EscapeMarkup() + "[/]");
                    AnsiConsole.MarkupLine("");
                }

                // Check if the model wants to call tools
                var responseToolCalls = streamedResult?.ToolCalls ?? result!.Value.ToolCalls;
                if (responseToolCalls.Count > 0)
                {
                    var toolCalls = responseToolCalls
                        .Select(NormalizeToolCall)
                        .ToList();
                    messages.Add(OpenAI.Chat.ChatMessage.CreateAssistantMessage(toolCalls.Select(call => call.ToolCall)));
                    requestMessages.Add(new ModelRequestMessage(
                        "assistant",
                        "",
                        toolCalls: toolCalls.Select(call => new ModelRequestToolCall(
                            call.ToolCall.Id,
                            call.ToolCall.FunctionName,
                            call.ArgumentsJson)).ToArray()));

                    foreach (var normalizedCall in toolCalls)
                    {
                        var toolCall = normalizedCall.ToolCall;
                        var toolName = toolCall.FunctionName;
                        var argsJson = normalizedCall.ArgumentsJson;

                        var preview = argsJson.Length > 80 ? argsJson.Substring(0, 80) + "..." : argsJson;
                        AnsiConsole.MarkupLine($"\n[dim]Tool: {toolName.EscapeMarkup()} ({preview.EscapeMarkup()})[/]");

                        var toolSw = System.Diagnostics.Stopwatch.StartNew();
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
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            AnsiConsole.WriteException(ex);

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

                        toolSw.Stop();
                        toolTime += toolSw.Elapsed;
                        toolCallCount++;

                        if (!string.IsNullOrEmpty(normalizedCall.Warning))
                            toolResult = normalizedCall.Warning + "\n\n" + toolResult;

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
                            AnsiConsole.MarkupLine($"[dim]Result ({toolSw.Elapsed.TotalSeconds:F1}s): {toolResult.Substring(0, 8000).EscapeMarkup()}... (truncated)[/]");
                        else
                            AnsiConsole.MarkupLine($"[dim]Result ({toolSw.Elapsed.TotalSeconds:F1}s): {toolResult.EscapeMarkup()}[/]");

                        // Truncate long results for the model
                        if (toolResult.Length > 120000)
                            toolResult = toolResult.Substring(0, 120000) + "\n... (truncated)";

                        messages.Add(OpenAI.Chat.ChatMessage.CreateToolMessage(toolCall.Id, toolResult));
                        requestMessages.Add(new ModelRequestMessage("tool", toolResult, toolCall.Id));

                        // Persist prior tool context as normal assistant text. OpenAI tool
                        // messages are only valid immediately after their matching assistant
                        // tool call inside the same request.
                        var toolCallContent = $"Tool call from prior turn: {toolName} with arguments: {argsJson}\nResult:\n{toolResult}";
                        history.Add(new ChatMessage("assistant", toolCallContent));
                        onHistoryChanged?.Invoke();
                    }

                    if (takeSteeringComments?.Invoke() is { Count: > 0 } steeringComments)
                    {
                        foreach (var comment in steeringComments)
                        {
                            var steeringMessage = $"User steering comment: {comment}";
                            history.Add(new ChatMessage("user", steeringMessage));
                            onHistoryChanged?.Invoke();
                            messages.Add(OpenAI.Chat.ChatMessage.CreateUserMessage(steeringMessage));
                            requestMessages.Add(new ModelRequestMessage("user", steeringMessage));
                        }
                    }

                    // Continue the loop to get the next response.
                    continue;
                }

                // Final text response — strip thinking tags if they're in the content
                var cleanContent = ChatResponseParsing.StripThinkingTags(contentText);
                AnsiConsole.WriteLine();
                await UiService.DisplayMarkdown(cleanContent);
                AnsiConsole.WriteLine();
                
                // Display TPS stats
                turnSw.Stop();
                if (totalTime.TotalSeconds > 0 && totalOutputTokens > 0)
                {
                    var tps = totalOutputTokens / totalTime.TotalSeconds;
                    AnsiConsole.MarkupLine($"[dim]⚡ {totalOutputTokens:N0} output tokens in {totalTime.TotalSeconds:F1}s ({tps:F1} tps) | {totalTokens:N0} total tokens[/]");
                    AnsiConsole.MarkupLine($"[dim]⏱ turn {turnSw.Elapsed.TotalSeconds:F1}s | model {totalTime.TotalSeconds:F1}s ({modelCalls} {(modelCalls == 1 ? "call" : "calls")}) | tools {toolTime.TotalSeconds:F1}s ({toolCallCount} {(toolCallCount == 1 ? "call" : "calls")})[/]");
                }
                else if (totalTokens > 0)
                {
                    AnsiConsole.MarkupLine($"[dim]📊 {totalTokens:N0} total tokens[/]");
                }
                
                history.Add(new ChatMessage("assistant", cleanContent));
                onHistoryChanged?.Invoke();
                messages.Add(OpenAI.Chat.ChatMessage.CreateAssistantMessage(cleanContent));

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
        CaptureRequest(history.Select(ToRequestSnapshotMessage), opts.Tools, streaming: false);
        var result = await CompleteChatWithRecoveryAsync(messages, opts, cancellationToken);

        if (!ChatResponseParsing.HasChoices(result))
            throw new InvalidOperationException("The model server returned an empty response (no choices).");

        string reasoningText = ChatResponseParsing.ExtractReasoningFromRawResponse(result);
        var contentText = ExtractText(result.Value.Content);

        // Fall back to extracting thinking tags from content
        if (string.IsNullOrEmpty(reasoningText))
            reasoningText = ChatResponseParsing.ExtractThinking(contentText);

        return (ChatResponseParsing.StripThinkingTags(contentText), reasoningText);
    }

    /// <summary>
    /// Retries temporary provider and transport failures in place. Keeping the
    /// same request messages is important in tool mode: the model can continue
    /// from tool results it already produced instead of restarting the task.
    /// </summary>
    private async Task<ClientResult<OpenAI.Chat.ChatCompletion>> CompleteChatWithRecoveryAsync(
        IEnumerable<OpenAI.Chat.ChatMessage> messages,
        OpenAI.Chat.ChatCompletionOptions options,
        CancellationToken cancellationToken)
    {
        for (var retry = 0; ; retry++)
        {
            try
            {
                return await _chatClient.CompleteChatAsync(messages, options, cancellationToken);
            }
            catch (Exception ex) when (
                !cancellationToken.IsCancellationRequested &&
                retry < MaxTransientModelRetries &&
                ChatResponseParsing.IsTransientModelFailure(ex))
            {
                var attempt = retry + 1;
                var delay = TimeSpan.FromSeconds(attempt);
                var reason = ChatResponseParsing.DescribeTransientModelFailure(ex);
                AnsiConsole.MarkupLine(
                    $"\n[yellow]Temporary model provider failure ({reason.EscapeMarkup()}); " +
                    $"continuing this task in {delay.TotalSeconds:0}s ({attempt}/{MaxTransientModelRetries}).[/]");
                await Task.Delay(delay, cancellationToken);
            }
        }
    }

    private async Task<StreamedChatCompletion> CompleteChatStreamingWithRecoveryAsync(
        IEnumerable<OpenAI.Chat.ChatMessage> messages,
        OpenAI.Chat.ChatCompletionOptions options,
        CancellationToken cancellationToken)
    {
        for (var retry = 0; ; retry++)
        {
            try
            {
                var content = new StringBuilder();
                var reasoningLoopDetector = new StreamingLoopDetector();
                var taggedContent = new ThinkingTagStreamParser();
                var toolCalls = new Dictionary<int, StreamingToolCallBuilder>();
                var totalTokens = 0;
                var outputTokens = 0;
                var finishReason = "";
                var showedReasoning = false;
                var stream = _chatClient.CompleteChatStreamingAsync(messages, options, cancellationToken);

                await foreach (var update in stream.WithCancellation(cancellationToken))
                {
                    var reasoningDelta = ChatResponseParsing.ExtractReasoningDelta(update);
                    if (reasoningDelta.Length > 0)
                    {
                        ThrowIfReasoningLoops(reasoningLoopDetector, reasoningDelta);
                        if (_thinkingEnabled)
                        {
                            if (!showedReasoning)
                            {
                                AnsiConsole.MarkupLine("\n[dim]Thinking:[/]");
                                showedReasoning = true;
                            }
                            AnsiConsole.Markup("[dim]" + reasoningDelta.EscapeMarkup() + "[/]");
                        }
                    }

                    var contentDelta = ExtractText(update.ContentUpdate);
                    content.Append(contentDelta);
                    foreach (var segment in taggedContent.Append(contentDelta))
                    {
                        if (segment.IsReasoning)
                            ThrowIfReasoningLoops(reasoningLoopDetector, segment.Text);
                    }

                    var updateFinishReason = ChatResponseParsing.ExtractFinishReasonDelta(update);
                    if (updateFinishReason.Length > 0)
                        finishReason = updateFinishReason;

                    foreach (var toolCallUpdate in update.ToolCallUpdates)
                    {
                        if (!toolCalls.TryGetValue(toolCallUpdate.Index, out var builder))
                        {
                            builder = new StreamingToolCallBuilder();
                            toolCalls.Add(toolCallUpdate.Index, builder);
                        }

                        if (!string.IsNullOrEmpty(toolCallUpdate.ToolCallId))
                            builder.Id = toolCallUpdate.ToolCallId;
                        if (!string.IsNullOrEmpty(toolCallUpdate.FunctionName))
                            builder.FunctionName.Append(toolCallUpdate.FunctionName);
                        builder.Arguments.Append(toolCallUpdate.FunctionArgumentsUpdate.ToString());
                    }

                    if (update.Usage is { } usage)
                    {
                        totalTokens = usage.TotalTokenCount;
                        outputTokens = usage.OutputTokenCount;
                    }
                }

                foreach (var segment in taggedContent.Complete())
                {
                    if (segment.IsReasoning)
                        ThrowIfReasoningLoops(reasoningLoopDetector, segment.Text);
                }

                if (showedReasoning)
                    AnsiConsole.WriteLine();

                var completedToolCalls = toolCalls
                    .OrderBy(pair => pair.Key)
                    .Select(pair => OpenAI.Chat.ChatToolCall.CreateFunctionToolCall(
                        pair.Value.Id,
                        pair.Value.FunctionName.ToString(),
                        BinaryData.FromString(pair.Value.Arguments.ToString())))
                    .ToList();

                return new StreamedChatCompletion(content.ToString(), completedToolCalls, totalTokens, outputTokens, finishReason);
            }
            catch (Exception ex) when (
                !cancellationToken.IsCancellationRequested &&
                retry < MaxTransientModelRetries &&
                ChatResponseParsing.IsTransientModelFailure(ex))
            {
                var attempt = retry + 1;
                var delay = TimeSpan.FromSeconds(attempt);
                var reason = ChatResponseParsing.DescribeTransientModelFailure(ex);
                AnsiConsole.MarkupLine(
                    $"\n[yellow]Temporary model provider failure ({reason.EscapeMarkup()}); " +
                    $"continuing this task in {delay.TotalSeconds:0}s ({attempt}/{MaxTransientModelRetries}).[/]");
                await Task.Delay(delay, cancellationToken);
            }
        }
    }

    /// <summary>
    /// Send a full conversation with streaming output.
    /// Returns the full response text and total token count.
    /// </summary>
    public async Task<(string Response, int TokenCount)> ChatStreamingAsync(List<ChatMessage> history, CancellationToken cancellationToken = default)
    {
        var messages = history.Select(m => ToRequestMessage(m)).ToList();
        var opts = new OpenAI.Chat.ChatCompletionOptions();
        ApplyThinkingOptions(opts);
        CaptureRequest(history.Select(ToRequestSnapshotMessage), opts.Tools, streaming: true);
        var stream = _chatClient.CompleteChatStreamingAsync(messages, opts, cancellationToken);

        var sb = new StringBuilder();
        int totalTokens = 0;
        int outputTokens = 0;
        var displayedSection = StreamDisplaySection.None;
        var taggedContent = new ThinkingTagStreamParser();
        var reasoningLoopDetector = new StreamingLoopDetector();
        var finishReason = "";
        var sw = System.Diagnostics.Stopwatch.StartNew();

        void DisplayReasoning(string text)
        {
            if (text.Length == 0)
                return;

            ThrowIfReasoningLoops(reasoningLoopDetector, text);

            if (!_thinkingEnabled)
                return;

            if (displayedSection != StreamDisplaySection.Reasoning)
            {
                AnsiConsole.MarkupLine(displayedSection == StreamDisplaySection.None
                    ? "\n[dim]Thinking:[/]"
                    : "\n\n[dim]Thinking:[/]");
                displayedSection = StreamDisplaySection.Reasoning;
            }

            AnsiConsole.Markup("[dim]" + text.EscapeMarkup() + "[/]");
        }

        void DisplayAnswer(string text)
        {
            if (text.Length == 0)
                return;

            if (displayedSection == StreamDisplaySection.Reasoning)
                AnsiConsole.MarkupLine("\n\n[green]Answer:[/]");

            displayedSection = StreamDisplaySection.Answer;
            sb.Append(text);
            AnsiConsole.Markup(text.EscapeMarkup());
        }

        void DisplayTaggedSegments(IReadOnlyList<StreamTextSegment> segments)
        {
            foreach (var segment in segments)
            {
                if (segment.IsReasoning)
                    DisplayReasoning(segment.Text);
                else
                    DisplayAnswer(segment.Text);
            }
        }

        await foreach (var update in stream.WithCancellation(cancellationToken))
        {
            var reasoningDelta = ChatResponseParsing.ExtractReasoningDelta(update);
            if (reasoningDelta.Length > 0)
                DisplayReasoning(reasoningDelta);

            if (update.ContentUpdate is not null)
            {
                var text = ExtractText(update.ContentUpdate);
                if (text.Length > 0)
                    DisplayTaggedSegments(taggedContent.Append(text));
            }

            if (update.Usage is { } usage)
            {
                totalTokens = usage.TotalTokenCount;
                outputTokens = usage.OutputTokenCount;
            }

            var updateFinishReason = ChatResponseParsing.ExtractFinishReasonDelta(update);
            if (updateFinishReason.Length > 0)
                finishReason = updateFinishReason;
        }
        DisplayTaggedSegments(taggedContent.Complete());
        sw.Stop();

        if (string.Equals(finishReason, "length", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "The model response was truncated because it reached the available context or output-token limit. " +
                "Increase the model context size or reduce the conversation before continuing.");
        }

        AnsiConsole.WriteLine();
        if (sw.Elapsed.TotalSeconds > 0 && outputTokens > 0)
        {
            var tps = outputTokens / sw.Elapsed.TotalSeconds;
            AnsiConsole.MarkupLine($"[dim]⚡ {outputTokens:N0} output tokens in {sw.Elapsed.TotalSeconds:F1}s ({tps:F1} tps) | {totalTokens:N0} total tokens[/]");
        }
        else if (totalTokens > 0)
        {
            AnsiConsole.MarkupLine($"[dim]📊 {totalTokens:N0} total tokens[/]");
        }

        return (sb.ToString(), totalTokens);
    }

    private static void ThrowIfReasoningLoops(StreamingLoopDetector detector, string text)
    {
        if (detector.Append(text) is { } repetition)
            throw new ReasoningLoopDetectedException(repetition);
    }

    private enum StreamDisplaySection
    {
        None,
        Reasoning,
        Answer,
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
        if (!JsonArgs.TryParseObject(argsJson, out var doc, out var argsError))
            return $"Error: tool_catalog expected JSON object arguments. {argsError}";

        using (doc)
        {
            foreach (var name in JsonArgs.StringArray(doc.RootElement, "enable", "tools", "tool", "names"))
            {
                if (string.IsNullOrWhiteSpace(name))
                    continue;

                if (_availableLocalTools.Contains(name))
                {
                    _activeLocalTools.Add(name);
                    enabled.Add(name);
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
    }

    private async Task<string> RunContextSummarize(string argsJson)
    {
        if (!JsonArgs.TryParseObject(argsJson, out var doc, out var argsError))
            return $"Error: ctx_summarize expected JSON object arguments. {argsError}";
        using var _ = doc;
        var root = doc.RootElement;
        var id = JsonArgs.String(root, "", "id", "contextId", "context_id", "key", "handle");
        var focus = JsonArgs.String(root, "", "focus", "query", "topic", "summaryFocus", "summary_focus");

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

    private void CaptureRequest(
        IEnumerable<ModelRequestMessage> messages,
        IEnumerable<OpenAI.Chat.ChatTool> tools,
        bool streaming)
    {
        var messageSnapshot = messages
            .Select(message => message with { ToolCalls = message.ToolCalls.ToArray() })
            .ToArray();
        var toolSnapshot = tools
            .Select(tool => new ModelRequestTool(
                tool.FunctionName,
                tool.FunctionDescription ?? "",
                tool.FunctionParameters?.ToString()))
            .ToArray();

        LastRequestSnapshot = new ModelRequestSnapshot(
            DateTimeOffset.UtcNow,
            _modelName,
            streaming,
            _temperature,
            _maxTokens,
            _thinkingEnabled && _isOModel ? "high" : null,
            messageSnapshot,
            toolSnapshot);
    }

    private static ModelRequestMessage ToRequestSnapshotMessage(ChatMessage message)
    {
        return message.Role switch
        {
            "system" => new ModelRequestMessage("system", message.Content),
            "assistant" => new ModelRequestMessage("assistant", message.Content),
            "tool" => new ModelRequestMessage("assistant", $"Prior tool result:\n{message.Content}"),
            _ => new ModelRequestMessage("user", message.Content),
        };
    }

    private static OpenAI.Chat.ChatMessage ToRequestMessage(ChatMessage msg)
    {
        return msg.Role switch
        {
            "system" => OpenAI.Chat.ChatMessage.CreateSystemMessage(msg.Content),
            "assistant" => OpenAI.Chat.ChatMessage.CreateAssistantMessage(msg.Content),
            "tool" => OpenAI.Chat.ChatMessage.CreateAssistantMessage($"Prior tool result:\n{msg.Content}"),
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

    private static NormalizedToolCall NormalizeToolCall(OpenAI.Chat.ChatToolCall toolCall)
    {
        var argsJson = toolCall.FunctionArguments.ToString();
        if (ChatResponseParsing.IsJsonObject(argsJson))
            return new NormalizedToolCall(toolCall, argsJson, "");

        var warning = string.IsNullOrWhiteSpace(argsJson)
            ? $"Warning: Tool '{toolCall.FunctionName}' was called with empty arguments. Retrying with an empty JSON object; provide the required arguments in the next tool call if this result is insufficient."
            : $"Warning: Tool '{toolCall.FunctionName}' was called with invalid arguments. Expected a JSON object, received: {ChatResponseParsing.TruncateForWarning(argsJson)}. Retrying with an empty JSON object; provide valid JSON arguments in the next tool call if this result is insufficient.";

        const string fallbackArguments = "{}";
        var sanitizedToolCall = OpenAI.Chat.ChatToolCall.CreateFunctionToolCall(
            toolCall.Id,
            toolCall.FunctionName,
            BinaryData.FromString(fallbackArguments));

        return new NormalizedToolCall(sanitizedToolCall, fallbackArguments, warning);
    }

    private sealed record NormalizedToolCall(
        OpenAI.Chat.ChatToolCall ToolCall,
        string ArgumentsJson,
        string Warning);

    private sealed record StreamedChatCompletion(
        string Content,
        IReadOnlyList<OpenAI.Chat.ChatToolCall> ToolCalls,
        int TotalTokenCount,
        int OutputTokenCount,
        string FinishReason);

    private sealed class StreamingToolCallBuilder
    {
        public string Id { get; set; } = "";
        public StringBuilder FunctionName { get; } = new();
        public StringBuilder Arguments { get; } = new();
    }
}
