using System.ClientModel;
using System.Text;
using OpenAI;
using Spectre.Console;

namespace picoNET.agent;

/// <summary>
/// Client wrapping the OpenAI SDK for chat completions.
/// Supports tool calling for bash, read, edit, write.
/// </summary>
internal class AgentClient
{
    private readonly OpenAI.Chat.ChatClient _chatClient;
    private readonly List<OpenAI.Chat.ChatTool>? _tools;

    public AgentClient(AgentConfig config, string[]? enabledTools = null)
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

        if (enabledTools?.Length > 0)
            _tools = ToolRegistry.GetTools(enabledTools);
    }

    /// <summary>
    /// Send a single prompt (no conversation history, no tools).
    /// </summary>
    public async Task<string> CompleteAsync(string prompt, string? systemPrompt = null)
    {
        var messages = new List<OpenAI.Chat.ChatMessage>();

        if (!string.IsNullOrEmpty(systemPrompt))
            messages.Add(OpenAI.Chat.ChatMessage.CreateSystemMessage(systemPrompt));

        messages.Add(OpenAI.Chat.ChatMessage.CreateUserMessage(prompt));

        var result = await _chatClient.CompleteChatAsync(messages);
        return ExtractText(result.Value.Content);
    }

    /// <summary>
    /// Chat with tool calling support. Loops through tool calls until the model
    /// returns a text response. Returns (responseText, totalTokenCount).
    /// </summary>
    public async Task<(string Response, int TokenCount)> ChatWithToolsAsync(List<ChatMessage> history)
    {
        var messages = history.Select(m => ToRequestMessage(m)).ToList();
        int totalTokens = 0;

        while (true)
        {
            var opts = new OpenAI.Chat.ChatCompletionOptions();
            foreach (var tool in _tools ?? Enumerable.Empty<OpenAI.Chat.ChatTool>())
                opts.Tools.Add(tool);

            var result = await _chatClient.CompleteChatAsync(messages, opts);

            totalTokens += result.Value.Usage?.TotalTokenCount ?? 0;

            // Check if the model wants to call tools
            if (result.Value.ToolCalls.Count > 0)
            {
                messages.Add(OpenAI.Chat.ChatMessage.CreateAssistantMessage(result.Value));

                foreach (var toolCall in result.Value.ToolCalls)
                {
                    var toolName = toolCall.FunctionName;
                    var argsJson = toolCall.FunctionArguments.ToString();

                    var preview = argsJson.Length > 80 ? argsJson.Substring(0, 80) + "..." : argsJson;
                    AnsiConsole.MarkupLine("\n[dim]Tool: " + toolName + " (" + preview + ")[/]");

                    var toolResult = ToolRegistry.Execute(toolName, argsJson);

                    // Display tool result
                    if (toolResult.Length > 4000)
                        AnsiConsole.MarkupLine("[dim]Result: " + toolResult.Substring(0, 4000) + "... (truncated)[/]");
                    else
                        AnsiConsole.MarkupLine("[dim]Result: " + toolResult + "[/]");

                    // Truncate long results for the model
                    if (toolResult.Length > 16000)
                        toolResult = toolResult.Substring(0, 16000) + "\n... (truncated)";

                    messages.Add(OpenAI.Chat.ChatMessage.CreateToolMessage(toolCall.Id, toolResult));
                }

                // Continue the loop to get the next response
                continue;
            }

            // Final text response
            var response = ExtractText(result.Value.Content);
            history.Add(new ChatMessage("assistant", response));
            messages.Add(OpenAI.Chat.ChatMessage.CreateAssistantMessage(result.Value));

            return (response, totalTokens);
        }
    }

    /// <summary>
    /// Send a full conversation and get a complete response (no tools).
    /// </summary>
    public async Task<string> ChatAsync(List<ChatMessage> history)
    {
        var messages = history.Select(m => ToRequestMessage(m)).ToList();
        var result = await _chatClient.CompleteChatAsync(messages);
        return ExtractText(result.Value.Content);
    }

    /// <summary>
    /// Send a full conversation with streaming output.
    /// Returns the full response text and total token count.
    /// </summary>
    public async Task<(string Response, int TokenCount)> ChatStreamingAsync(List<ChatMessage> history)
    {
        var messages = history.Select(m => ToRequestMessage(m)).ToList();
        var stream = _chatClient.CompleteChatStreamingAsync(messages);

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
}
