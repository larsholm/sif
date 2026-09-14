using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using OpenAI.Chat;

namespace sif.agent;

internal static class OpenAICompatibleStreaming
{
    // Read raw events before SDK deserialization: compatible providers can send
    // finish_reason "error", which the SDK's enum cannot represent.
    public static async IAsyncEnumerable<StreamingChatCompletionUpdate> ReadAsync(
        AsyncCollectionResult<StreamingChatCompletionUpdate> updates,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var page in updates.GetRawPagesAsync().WithCancellation(cancellationToken))
        {
            using var response = page.GetRawResponse();
            var stream = response.ContentStream
                ?? throw new IOException("The model provider returned no response stream.");

            await foreach (var item in SseParser.Create(stream).EnumerateAsync(cancellationToken))
            {
                if (item.Data == "[DONE]")
                    yield break;
                if (string.IsNullOrWhiteSpace(item.Data))
                    continue;

                if (ProviderCompletionError.TryParse(item.Data, out var error))
                    throw new ProviderStreamException(error!, item.Data, response);

                yield return ModelReaderWriter.Read<StreamingChatCompletionUpdate>(
                    BinaryData.FromString(item.Data), new ModelReaderWriterOptions("W"))!;
            }
        }
    }
}

internal sealed class ProviderStreamException : ClientResultException
{
    // Keep the failing event independently of the disposed, unbuffered stream
    // so error formatting, retry classification, and debug logging retain it.
    public string RawEvent { get; }

    public ProviderStreamException(ProviderCompletionError error, string rawEvent, PipelineResponse response)
        : base(string.IsNullOrWhiteSpace(error.Message)
            ? "The model provider stopped generation with an unspecified error."
            : $"The model provider stopped generation: {error.Message}", response)
    {
        RawEvent = rawEvent;
    }
}
