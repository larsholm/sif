using System.ClientModel;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace sif.agent;

internal static class AgentErrorFormatter
{
    public static string ToUserMessage(Exception ex)
    {
        if (TryFind<ClientResultException>(ex, out var clientEx) && clientEx is not null)
            return FormatClientResult(clientEx);

        var hasHttpError = TryFind<HttpRequestException>(ex, out var httpEx);
        if (hasHttpError ||
            TryFind<SocketException>(ex, out _) ||
            ex.Message.Contains("Connection refused", StringComparison.OrdinalIgnoreCase))
        {
            return "Could not reach the model endpoint. Check that the configured server is running and that the base URL is correct."
                + Detail(httpEx?.Message ?? ex.Message);
        }

        return ex.Message;
    }

    private static string FormatClientResult(ClientResultException ex)
    {
        var status = TryGetStatus(ex);
        var response = TryReadResponse(ex);
        var combined = $"{ex.Message}\n{response}";

        if (status == 401)
            return "Authentication failed (HTTP 401). Check the API key for the active model profile."
                + Detail(ExtractProviderMessage(response));

        if (status == 402)
            return "The provider rejected the request for billing or quota reasons (HTTP 402). Add credits, raise the key limit, or lower max tokens."
                + Detail(ExtractProviderMessage(response));

        if (status == 429)
            return "The provider rate-limited the request (HTTP 429). Wait a bit or switch to another model/provider."
                + Detail(ExtractProviderMessage(response));

        if (status >= 500 && LooksLikeToolParseFailure(combined))
            return "The provider rejected a malformed tool call. Sif retries this once in tool mode; if it repeats, switch model/provider or disable tools for this request."
                + Detail(ExtractProviderMessage(response));

        if (status >= 500)
            return $"The model provider returned HTTP {status}. This is usually a provider-side failure; retry or switch model/provider."
                + Detail(ExtractProviderMessage(response));

        if (status > 0)
            return $"The model provider rejected the request (HTTP {status})."
                + Detail(ExtractProviderMessage(response));

        return ex.Message;
    }

    private static bool LooksLikeToolParseFailure(string text)
    {
        return text.Contains("failed to parse input", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("failed to parse tool call", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("<tool_call>", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("<function=", StringComparison.OrdinalIgnoreCase);
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

    private static string TryReadResponse(ClientResultException ex)
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

    private static string ExtractProviderMessage(string response)
    {
        if (string.IsNullOrWhiteSpace(response))
            return "";

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(response);
            if (doc.RootElement.TryGetProperty("error", out var error))
            {
                if (error.TryGetProperty("message", out var message) && message.ValueKind == System.Text.Json.JsonValueKind.String)
                    return message.GetString() ?? "";
            }
        }
        catch
        {
        }

        return "";
    }

    private static string Detail(string detail)
    {
        if (string.IsNullOrWhiteSpace(detail))
            return "";

        detail = detail.Replace('\r', ' ').Replace('\n', ' ').Trim();
        detail = Regex.Replace(detail, @"https?://\S+", "[link]");
        if (detail.Length > 240)
            detail = detail[..240] + "...";

        return $" Detail: {detail}";
    }

    private static bool TryFind<T>(Exception ex, out T? found) where T : Exception
    {
        for (var current = ex; current != null; current = current.InnerException!)
        {
            if (current is T typed)
            {
                found = typed;
                return true;
            }

            if (current is AggregateException aggregate)
            {
                foreach (var inner in aggregate.Flatten().InnerExceptions)
                {
                    if (TryFind(inner, out found))
                        return true;
                }
            }
        }

        found = null;
        return false;
    }
}
