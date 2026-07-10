using System.Text.Json;

namespace sif.agent.Services;

/// <summary>
/// Durable, per-conversation storage. The session index is deliberately kept
/// separate from message payloads so the resume list never has to load every
/// saved conversation into memory.
/// </summary>
internal sealed class ConversationStore
{
    private const string ActiveStatus = "active";
    private const string ClosedStatus = "closed";
    private static readonly object FileLock = new();
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };
    private readonly string _rootPath;

    private ConversationStore(string rootPath, ConversationSession session)
    {
        _rootPath = rootPath;
        Session = session;
    }

    public ConversationSession Session { get; private set; }

    private string SessionPath => Path.Combine(_rootPath, Session.Id);
    private string MetadataPath => Path.Combine(SessionPath, "session.json");
    private string HistoryPath => Path.Combine(SessionPath, "history.json");

    public static ConversationStore Create(string? model = null)
    {
        var now = DateTimeOffset.UtcNow;
        var session = new ConversationSession(
            "chat_" + Guid.NewGuid().ToString("N")[..12],
            now.ToString("O"),
            now.ToString("O"),
            ActiveStatus,
            0,
            "New conversation",
            model,
            ContextStore.GetSessionId());
        var store = new ConversationStore(DefaultRootPath, session);
        store.Save([]);
        return store;
    }

    // Test-only overload: production callers should use the default user data path.
    internal static ConversationStore Create(string rootPath, string? model)
    {
        var now = DateTimeOffset.UtcNow;
        var session = new ConversationSession(
            "chat_" + Guid.NewGuid().ToString("N")[..12],
            now.ToString("O"),
            now.ToString("O"),
            ActiveStatus,
            0,
            "New conversation",
            model,
            ContextStore.GetSessionId());
        var store = new ConversationStore(rootPath, session);
        store.Save([]);
        return store;
    }

    public void Save(IReadOnlyList<ChatMessage> history)
    {
        var messages = history.Select(message => new StoredChatMessage(message.Role, message.Content, message.ToolCallId)).ToList();
        var now = DateTimeOffset.UtcNow.ToString("O");
        Session = Session with
        {
            UpdatedAt = now,
            Status = ActiveStatus,
            MessageCount = messages.Count,
            Preview = MakePreview(messages)
        };

        lock (FileLock)
        {
            Directory.CreateDirectory(SessionPath);
            WriteJsonAtomically(HistoryPath, messages);
            WriteJsonAtomically(MetadataPath, Session);
        }
    }

    public void Close()
    {
        Session = Session with { Status = ClosedStatus, UpdatedAt = DateTimeOffset.UtcNow.ToString("O") };
        WriteMetadata();
    }

    public static IReadOnlyList<ConversationSession> List()
        => List(DefaultRootPath);

    internal static IReadOnlyList<ConversationSession> List(string rootPath)
    {
        if (!Directory.Exists(rootPath))
            return [];

        var sessions = new List<ConversationSession>();
        foreach (var path in Directory.EnumerateFiles(rootPath, "session.json", SearchOption.AllDirectories))
        {
            TryReadMetadata(path, sessions);
        }

        return sessions
            .OrderByDescending(session => ParseTimestamp(session.UpdatedAt))
            .ThenByDescending(session => session.Id, StringComparer.Ordinal)
            .ToList();
    }

    public static ConversationSession? FindMostRecentActive()
        => List().FirstOrDefault(session => string.Equals(session.Status, ActiveStatus, StringComparison.OrdinalIgnoreCase));

    public static bool TryOpen(string id, out ConversationStore? store, out List<ChatMessage>? history, out string error)
        => TryOpen(DefaultRootPath, id, out store, out history, out error);

    internal static bool TryOpen(string rootPath, string id, out ConversationStore? store, out List<ChatMessage>? history, out string error)
    {
        store = null;
        history = null;
        error = "";

        var matches = List(rootPath)
            .Where(session => session.Id.Equals(id, StringComparison.OrdinalIgnoreCase) ||
                              session.Id.StartsWith(id, StringComparison.OrdinalIgnoreCase) ||
                              (session.Id.StartsWith("chat_", StringComparison.OrdinalIgnoreCase) &&
                               session.Id["chat_".Length..].StartsWith(id, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        if (matches.Count == 0)
        {
            error = $"No saved conversation matches '{id}'.";
            return false;
        }

        if (matches.Count > 1)
        {
            error = $"More than one saved conversation matches '{id}'. Use a longer id.";
            return false;
        }

        var session = matches[0];
        var historyPath = Path.Combine(rootPath, session.Id, "history.json");
        try
        {
            if (!File.Exists(historyPath))
            {
                error = $"Saved conversation '{session.Id}' has no history file.";
                return false;
            }

            var saved = JsonSerializer.Deserialize<List<StoredChatMessage>>(File.ReadAllText(historyPath), JsonOptions);
            if (saved == null)
            {
                error = $"Saved conversation '{session.Id}' could not be read.";
                return false;
            }

            history = saved.Select(message => new ChatMessage(message.Role, message.Content, message.ToolCallId)).ToList();
            if (!string.IsNullOrWhiteSpace(session.ContextSessionId))
                ContextStore.UseSession(session.ContextSessionId);
            store = new ConversationStore(rootPath, session with { Status = ActiveStatus });
            store.WriteMetadata();
            return true;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or ArgumentException)
        {
            error = $"Could not load saved conversation '{session.Id}': {ex.Message}";
            return false;
        }
    }

    private void WriteMetadata()
    {
        lock (FileLock)
        {
            Directory.CreateDirectory(SessionPath);
            WriteJsonAtomically(MetadataPath, Session);
        }
    }

    private static string DefaultRootPath
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".sif", "conversations");

    private static void TryReadMetadata(string path, ICollection<ConversationSession> sessions)
    {
        try
        {
            var session = JsonSerializer.Deserialize<ConversationSession>(File.ReadAllText(path), JsonOptions);
            if (session != null && !string.IsNullOrWhiteSpace(session.Id))
                sessions.Add(session);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // A corrupt or half-written session must not prevent the user from
            // resuming any other conversation.
        }
    }

    private static void WriteJsonAtomically<T>(string path, T value)
    {
        var temporaryPath = path + ".tmp";
        var json = JsonSerializer.Serialize(value, JsonOptions);
        using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(stream))
        {
            writer.Write(json);
            writer.Flush();
            stream.Flush(flushToDisk: true);
        }

        File.Move(temporaryPath, path, overwrite: true);
    }

    private static string MakePreview(IReadOnlyList<StoredChatMessage> messages)
    {
        var firstUserMessage = messages.FirstOrDefault(message => message.Role.Equals("user", StringComparison.OrdinalIgnoreCase));
        var text = firstUserMessage?.Content ?? messages.LastOrDefault()?.Content ?? "New conversation";
        var normalized = text.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ').Trim();
        return normalized.Length <= 120 ? normalized : normalized[..117] + "...";
    }

    private static DateTimeOffset ParseTimestamp(string timestamp)
        => DateTimeOffset.TryParse(timestamp, out var value) ? value : DateTimeOffset.MinValue;
}

internal sealed record ConversationSession(
    string Id,
    string CreatedAt,
    string UpdatedAt,
    string Status,
    int MessageCount,
    string Preview,
    string? Model,
    string? ContextSessionId = null);

internal sealed record StoredChatMessage(string Role, string Content, string? ToolCallId);
