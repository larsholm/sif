using System.ClientModel.Primitives;

namespace sif.agent;

/// <summary>
/// Removes the credential header added by the OpenAI SDK when an
/// OpenAI-compatible endpoint is intentionally configured without an API key.
/// </summary>
internal sealed class RemoveAuthorizationPolicy : PipelinePolicy
{
    public override void Process(
        PipelineMessage message,
        IReadOnlyList<PipelinePolicy> pipeline,
        int currentIndex)
    {
        message.Request.Headers.Remove("Authorization");
        ProcessNext(message, pipeline, currentIndex);
    }

    public override async ValueTask ProcessAsync(
        PipelineMessage message,
        IReadOnlyList<PipelinePolicy> pipeline,
        int currentIndex)
    {
        message.Request.Headers.Remove("Authorization");
        await ProcessNextAsync(message, pipeline, currentIndex).ConfigureAwait(false);
    }
}
