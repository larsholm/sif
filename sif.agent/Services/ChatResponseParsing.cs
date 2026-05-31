using System.ClientModel;
#pragma warning disable OPENAI001
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
               text.Contains("<tool_call>", StringComparison.Ordinal) ||
               text.Contains("<function=", StringComparison.Ordinal);
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
