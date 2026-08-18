namespace sif.agent;

internal sealed record ModelRequestSnapshot(
    DateTimeOffset CapturedAt,
    string Model,
    bool Streaming,
    float? Temperature,
    int? MaxOutputTokens,
    string? ReasoningEffort,
    IReadOnlyList<ModelRequestMessage> Messages,
    IReadOnlyList<ModelRequestTool> Tools)
{
    public int ApproximateInputCharacters =>
        Messages.Sum(message => message.Content.Length + (message.ToolCallId?.Length ?? 0) +
            message.ToolCalls.Sum(call => call.Id.Length + call.Name.Length + call.Arguments.Length)) +
        Tools.Sum(tool => tool.Name.Length + tool.Description.Length + (tool.ParametersJson?.Length ?? 0));
}

internal sealed record ModelRequestMessage
{
    public string Role { get; }
    public string Content { get; }
    public string? ToolCallId { get; }
    public IReadOnlyList<ModelRequestToolCall> ToolCalls { get; init; }

    public ModelRequestMessage(
        string role,
        string content,
        string? toolCallId = null,
        IReadOnlyList<ModelRequestToolCall>? toolCalls = null)
    {
        Role = role;
        Content = content;
        ToolCallId = toolCallId;
        ToolCalls = toolCalls ?? [];
    }
}

internal sealed record ModelRequestToolCall(string Id, string Name, string Arguments);

internal sealed record ModelRequestTool(string Name, string Description, string? ParametersJson);
