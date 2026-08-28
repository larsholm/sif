using System.ClientModel;
using System.ClientModel.Primitives;
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
    private static readonly string[] ReasoningPropertyNames = ["reasoning", "reasoning_content"];
    private static readonly ModelReaderWriterOptions WireFormat = new("W");

    /// <summary>
    /// Extract a reasoning field from a raw API response.
    /// Compatible providers return it separately on the choice message.
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
                var text = ExtractReasoningText(message);
                if (!string.IsNullOrEmpty(text))
                    return text.Trim();
            }
        }
        catch
        {
            // Parsing failed — no reasoning available
        }
        return "";
    }

    /// <summary>
    /// Extract a reasoning delta retained by the SDK as additional raw response data.
    /// OpenAI-compatible providers commonly use either <c>reasoning</c> or
    /// <c>reasoning_content</c> on the streamed choice delta.
    /// </summary>
    public static string ExtractReasoningDelta(OpenAI.Chat.StreamingChatCompletionUpdate update)
    {
        try
        {
            // The SDK does not expose provider-specific reasoning fields as public
            // properties, but it preserves them when a wire model is re-serialized.
            var json = ModelReaderWriter.Write(update, WireFormat).ToString();
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("choices", out var choices) ||
                choices.ValueKind != JsonValueKind.Array ||
                choices.GetArrayLength() == 0)
            {
                return "";
            }

            var choice = choices[0];
            if (!choice.TryGetProperty("delta", out var delta))
                return "";

            return ExtractReasoningText(delta);
        }
        catch
        {
            // A provider-specific delta must never break normal answer streaming.
            return "";
        }
    }

    private static string ExtractReasoningText(JsonElement container)
    {
        foreach (var propertyName in ReasoningPropertyNames)
        {
            if (!container.TryGetProperty(propertyName, out var reasoning))
                continue;

            if (reasoning.ValueKind == JsonValueKind.String)
                return reasoning.GetString() ?? "";
        }

        return "";
    }

    /// <summary>
    /// Extract the provider's finish reason from a streamed update. Keeping this
    /// on the wire representation avoids losing OpenAI-compatible finish reasons
    /// that the SDK does not otherwise expose consistently.
    /// </summary>
    public static string ExtractFinishReasonDelta(OpenAI.Chat.StreamingChatCompletionUpdate update)
    {
        try
        {
            var json = ModelReaderWriter.Write(update, WireFormat).ToString();
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("choices", out var choices) &&
                choices.ValueKind == JsonValueKind.Array &&
                choices.GetArrayLength() > 0 &&
                choices[0].TryGetProperty("finish_reason", out var finishReason) &&
                finishReason.ValueKind == JsonValueKind.String)
            {
                return finishReason.GetString() ?? "";
            }
        }
        catch
        {
            // Missing provider metadata is not itself a completion failure.
        }
        return "";
    }

    /// <summary>
    /// Extract the finish reason from a non-streamed completion response.
    /// </summary>
    public static string ExtractFinishReason(ClientResult<OpenAI.Chat.ChatCompletion> result)
    {
        try
        {
            var json = result.GetRawResponse().Content.ToString();
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("choices", out var choices) &&
                choices.ValueKind == JsonValueKind.Array &&
                choices.GetArrayLength() > 0 &&
                choices[0].TryGetProperty("finish_reason", out var finishReason) &&
                finishReason.ValueKind == JsonValueKind.String)
            {
                return finishReason.GetString() ?? "";
            }
        }
        catch
        {
            // Missing provider metadata is not itself a completion failure.
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

            if (IsModelRuntimeUnavailable(clientEx))
                return true;

            if (TryReadProviderCompletionError(clientEx) is { } providerError &&
                IsTransientProviderCompletionError(providerError))
            {
                return true;
            }

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

    /// <summary>
    /// Identifies responses emitted while a local model runtime is restarting or
    /// loading. Some OpenAI-compatible routers report this temporary state as an
    /// HTTP 400 even though retrying after the model reloads is safe.
    /// </summary>
    public static bool IsModelRuntimeUnavailable(Exception ex)
    {
        if (ex is ClientResultException clientEx)
        {
            var text = (clientEx.Message + "\n" + TryReadRawResponse(clientEx)).ToLowerInvariant();
            if ((text.Contains("engine protocol", StringComparison.Ordinal) &&
                 text.Contains("fetch failed", StringComparison.Ordinal)) ||
                text.Contains("model is loading", StringComparison.Ordinal) ||
                text.Contains("model currently loading", StringComparison.Ordinal) ||
                text.Contains("model runtime is unavailable", StringComparison.Ordinal))
            {
                return true;
            }
        }

        if (ex is AggregateException aggregate &&
            aggregate.InnerExceptions.Any(IsModelRuntimeUnavailable))
        {
            return true;
        }

        return ex.InnerException is not null && IsModelRuntimeUnavailable(ex.InnerException);
    }

    public static ProviderCompletionError? TryReadProviderCompletionError(ClientResultException ex)
    {
        var response = TryReadRawResponse(ex);
        try
        {
            return ProviderCompletionError.TryParse(response, out var providerError) ? providerError : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool IsTransientProviderCompletionError(ProviderCompletionError error)
    {
        if (error.Code is 408 or 429 || error.Code >= 500)
            return true;

        return error.Type.ToLowerInvariant() switch
        {
            "rate_limit_exceeded" or
            "provider_overloaded" or
            "provider_unavailable" or
            "timeout" or
            "server" => true,
            _ => false
        };
    }

    public static string DescribeTransientModelFailure(Exception ex)
    {
        if (IsModelRuntimeUnavailable(ex))
            return "model runtime unavailable";

        if (TryFindClientResultException(ex) is { } providerEx &&
            TryReadProviderCompletionError(providerEx) is { } providerError)
        {
            if (!string.IsNullOrWhiteSpace(providerError.Type))
                return providerError.Type.Replace('_', ' ');
            if (providerError.Code is { } code)
                return $"provider error {code}";
        }

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
        const string completeThinkingBlock = @"<(thinking|thought|reasoning|think)>.*?</\1>\s*";
        const string orphanThinkingTag = @"<\/?(?:thinking|thought|reasoning|think)>\s*";
        text = System.Text.RegularExpressions.Regex.Replace(
            text,
            completeThinkingBlock,
            "",
            System.Text.RegularExpressions.RegexOptions.Singleline | System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return System.Text.RegularExpressions.Regex.Replace(
            text,
            orphanThinkingTag,
            "",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    public static string ExtractThinking(string text)
    {
        const string completeThinkingBlock = @"<(thinking|thought|reasoning|think)>(.*?)</\1>";
        var matches = System.Text.RegularExpressions.Regex.Matches(
            text,
            completeThinkingBlock,
            System.Text.RegularExpressions.RegexOptions.Singleline | System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return string.Join('\n', matches.Select(match => match.Groups[2].Value));
    }
}
