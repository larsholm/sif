using System.ClientModel;
#pragma warning disable OPENAI001
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace sif.agent;

/// <summary>
/// Pure helpers for parsing chat completion responses: extracting text,
/// reasoning, and detecting provider tool-call parse errors.
/// </summary>
internal static class ChatResponseParsing
{
    /// <summary>
    /// Extract the "reasoning" field from a raw API response.
    /// vLLM / Qwen return reasoning as a separate field on the choice message.
    /// </summary>
    public static string ExtractReasoningFromRawResponse(ClientResult<OpenAI.Chat.ChatCompletion> result)
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
    /// Returns whether the raw response contains a non-empty <c>choices</c> array.
    /// Local OpenAI-compatible servers (vLLM, llama.cpp, …) occasionally return a
    /// response with an empty or missing <c>choices</c> array. The SDK's flattened
    /// accessors (<c>Content</c>, <c>ToolCalls</c>, <c>Role</c>, …) all dereference
    /// <c>Choices[0]</c> and would throw <see cref="ArgumentOutOfRangeException"/>.
    /// Defaults to <c>true</c> when the response can't be parsed, so this never
    /// suppresses an otherwise valid completion.
    /// </summary>
    public static bool HasChoices(ClientResult<OpenAI.Chat.ChatCompletion> result)
    {
        try
        {
            var json = result.GetRawResponse().Content.ToString();
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("choices", out var choices) &&
                choices.ValueKind == JsonValueKind.Array)
            {
                return choices.GetArrayLength() > 0;
            }
        }
        catch
        {
            // Couldn't parse — assume choices are present and let normal handling proceed.
        }
        return true;
    }

    public static bool IsJsonObject(string text)
    {
        if (!JsonArgs.TryParseObject(text, out var doc, out _))
            return false;

        using (doc)
            return true;
    }

    public static bool IsProviderToolParseError(ClientResultException ex)
    {
        var response = TryReadRawResponse(ex);
        var text = (ex.Message + "\n" + response).ToLowerInvariant();
        return text.Contains("failed to parse input", StringComparison.Ordinal) ||
               text.Contains("failed to parse tool call", StringComparison.Ordinal) ||
               text.Contains("failed to parse tool call arguments", StringComparison.Ordinal) ||
               text.Contains("attempting to parse an empty input", StringComparison.Ordinal) ||
               text.Contains("<tool_call>", StringComparison.Ordinal) ||
               text.Contains("<function=", StringComparison.Ordinal);
    }

    /// <summary>
    /// Returns whether a failed model request is safe to retry without changing
    /// the conversation. This includes temporary provider statuses and transport
    /// failures, but deliberately excludes malformed tool-call responses because
    /// those need the stricter recovery prompt used by the agent loop.
    /// </summary>
    public static bool IsTransientModelFailure(Exception ex)
    {
        if (ex is ClientResultException clientEx)
        {
            if (IsProviderToolParseError(clientEx))
                return false;

            var status = TryGetStatus(clientEx);
            if (status is 408 or 429 or 500 or 502 or 503 or 504)
                return true;
        }

        if (ex is AggregateException aggregate &&
            aggregate.InnerExceptions.Any(IsTransientModelFailure))
        {
            return true;
        }

        if (ex is HttpRequestException or SocketException or IOException or TaskCanceledException)
            return true;

        return ex.InnerException is not null && IsTransientModelFailure(ex.InnerException);
    }

    public static string DescribeTransientModelFailure(Exception ex)
    {
        if (TryFindClientResultException(ex) is { } clientEx && TryGetStatus(clientEx) is > 0 and var status)
            return $"HTTP {status}";

        return ex is TaskCanceledException || ex.InnerException is TaskCanceledException
            ? "request timeout"
            : "connection failure";
    }

    private static ClientResultException? TryFindClientResultException(Exception ex)
    {
        if (ex is ClientResultException clientEx)
            return clientEx;

        if (ex is AggregateException aggregate)
        {
            foreach (var inner in aggregate.InnerExceptions)
            {
                if (TryFindClientResultException(inner) is { } found)
                    return found;
            }
        }

        return ex.InnerException is null ? null : TryFindClientResultException(ex.InnerException);
    }

    private static int TryGetStatus(ClientResultException ex)
    {
        try
        {
            return ex.GetRawResponse()?.Status ?? 0;
        }
        catch
        {
            return 0;
        }
    }

    public static string TryReadRawResponse(ClientResultException ex)
    {
        try
        {
            return ex.GetRawResponse()?.Content.ToString() ?? "";
        }
        catch
        {
            return "";
        }
    }

    public static string TruncateForWarning(string text)
    {
        text = text.Replace("\r", "\\r").Replace("\n", "\\n");
        return text.Length > 200 ? text[..200] + "..." : text;
    }

    public static string StripThinkingTags(string text)
    {
        // Strip <thinking>...</thinking>, <thought>...</thought>, etc.
        text = System.Text.RegularExpressions.Regex.Replace(text, @"<\/?(?:thinking|thought|reasoning|think)>\s*", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return text;
    }

    public static string ExtractThinking(string text)
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
