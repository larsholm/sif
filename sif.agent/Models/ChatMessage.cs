namespace sif.agent;

/// <summary>
/// Represents a chat message in the conversation.
/// </summary>
internal class ChatMessage
{
    public string Role { get; }
    public string Content { get; }

    public ChatMessage(string role, string content)
    {
        Role = role.ToLowerInvariant();
        Content = content;
    }
}
