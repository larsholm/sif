using System.ClientModel;
using System.ClientModel.Primitives;
using System.Text.Json;

namespace sif.agent;

/// <summary>
/// Converts provider errors embedded in otherwise-successful chat completion
/// responses into <see cref="ClientResultException"/> before the OpenAI SDK
/// attempts to deserialize non-standard values such as finish_reason "error".
/// </summary>
internal sealed class OpenAICompatibleErrorPolicy : PipelinePolicy
{
    public override void Process(
        PipelineMessage message,
        IReadOnlyList<PipelinePolicy> pipeline,
        int currentIndex)
    {
        ProcessNext(message, pipeline, currentIndex);
        ThrowIfEmbeddedCompletionError(message);
    }

    public override async ValueTask ProcessAsync(
        PipelineMessage message,
        IReadOnlyList<PipelinePolicy> pipeline,
        int currentIndex)
    {
        await ProcessNextAsync(message, pipeline, currentIndex).ConfigureAwait(false);
        ThrowIfEmbeddedCompletionError(message);
    }

    private static void ThrowIfEmbeddedCompletionError(PipelineMessage message)
    {
        var response = message.Response;
        if (response is null || response.IsError || !message.BufferResponse)
            return;

        try
        {
            if (ProviderCompletionError.TryParse(response.Content.ToString(), out _))
                throw new ClientResultException(response);
        }
        catch (JsonException)
        {
            // Leave malformed or non-JSON success responses to the SDK's normal handling.
        }
    }
}

internal sealed record ProviderCompletionError(int? Code, string Type, string Message)
{
    public static bool TryParse(string json, out ProviderCompletionError? providerError)
    {
        providerError = null;
        if (string.IsNullOrWhiteSpace(json))
            return false;

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            return false;

        if (!HasErrorFinishReason(root, out var choiceError))
            return false;

        JsonElement error;
        if (choiceError is { } embedded)
            error = embedded;
        else if (!root.TryGetProperty("error", out error) || error.ValueKind != JsonValueKind.Object)
        {
            providerError = new ProviderCompletionError(null, "", "");
            return true;
        }

        int? code = null;
        if (error.TryGetProperty("code", out var codeElement))
        {
            if (codeElement.ValueKind == JsonValueKind.Number && codeElement.TryGetInt32(out var numericCode))
                code = numericCode;
            else if (codeElement.ValueKind == JsonValueKind.String &&
                     int.TryParse(codeElement.GetString(), out numericCode))
                code = numericCode;
        }

        var type = ReadString(error, "type");
        if (error.TryGetProperty("metadata", out var metadata) && metadata.ValueKind == JsonValueKind.Object)
            type = ReadString(metadata, "error_type") is { Length: > 0 } errorType ? errorType : type;

        providerError = new ProviderCompletionError(code, type, ReadString(error, "message"));
        return true;
    }

    private static bool HasErrorFinishReason(JsonElement root, out JsonElement? choiceError)
    {
        choiceError = null;
        if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array)
            return false;

        foreach (var choice in choices.EnumerateArray())
        {
            if (!choice.TryGetProperty("finish_reason", out var finishReason) ||
                finishReason.ValueKind != JsonValueKind.String ||
                !string.Equals(finishReason.GetString(), "error", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (choice.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
                choiceError = error;

            return true;
        }

        return false;
    }

    private static string ReadString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? ""
            : "";
    }
}
