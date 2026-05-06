using System.ClientModel;
using System.Text;
using OpenAI;
using Spectre.Console;

namespace picoNET.agent;

/// <summary>
/// Client wrapping the OpenAI SDK for chat completions.
/// Supports any OpenAI-compatible API endpoint (Ollama, LM Studio, etc.).
/// </summary>
internal class AgentClient
{
    private readonly OpenAI.Chat.ChatClient _chatClient;

    public AgentClient(AgentConfig config)
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
    }

    /// <summary>
    /// Send a single prompt (no conversation history).
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
    /// Send a full conversation and get a complete response.
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
