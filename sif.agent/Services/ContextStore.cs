using System.Text;
using System.Text.Json;

namespace sif.agent;

internal sealed record ContextEntry(
    string Id,
    string Source,
    string CreatedAt,
    int Length,
    string Preview,
    string Path);

internal sealed record ContextSearchHit(
    string Id,
    string Source,
    int Length,
    int Score,
    string Snippet);

/// <summary>
/// Stores large context out-of-band and provides focused retrieval by handle/search.
/// </summary>
internal static class ContextStore
{
    public const int AutoStoreThreshold = 12000;
    private const int PreviewLength = 800;
    private static readonly object Lock = new();
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };
    private static readonly string SessionId = DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss");

    private static string RootPath
    {
        get
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, ".sif", "context", SessionId);
        }
    }

    private static string IndexPath => Path.Combine(RootPath, "index.jsonl");
    private static string BlobsPath => Path.Combine(RootPath, "blobs");

    public static ContextEntry Store(string source, string content)
    {
        Directory.CreateDirectory(BlobsPath);

        var id = "ctx_" + Guid.NewGuid().ToString("N")[..12];
        var path = Path.Combine(BlobsPath, id + ".txt");
        File.WriteAllText(path, content);

        var entry = new ContextEntry(
            id,
            source,
            DateTimeOffset.UtcNow.ToString("O"),
            content.Length,
            MakePreview(content, PreviewLength),
            path);

        lock (Lock)
        {
            File.AppendAllText(IndexPath, JsonSerializer.Serialize(entry, JsonOptions) + Environment.NewLine);
        }

        return entry;
    }

    public static string StoreAndDescribe(string source, string content)
    {
        var entry = Store(source, content);
        return FormatStoredResult(entry);
    }

    public static string Search(string query, int limit = 8)
    {
        query = query.Trim();
        if (query.Length == 0)
            return "Error: query is required.";

        var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(t => t.Length > 0)
            .ToArray();

        var hits = LoadEntries()
            .Select(entry => ScoreEntry(entry, terms))
            .Where(hit => hit.Score > 0)
            .OrderByDescending(hit => hit.Score)
            .ThenBy(hit => hit.Source, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Clamp(limit, 1, 50))
            .ToList();

        if (hits.Count == 0)
            return $"No context hits for: {query}";

        var sb = new StringBuilder();
        sb.AppendLine($"Context search results for: {query}");
        foreach (var hit in hits)
        {
            sb.AppendLine();
            sb.AppendLine($"[{hit.Id}] {hit.Source} ({hit.Length:N0} chars, score {hit.Score})");
            sb.AppendLine(hit.Snippet);
        }

        return sb.ToString().TrimEnd();
    }

    public static string Read(string id, string? query = null, int maxChars = 6000)
    {
        var entry = LoadEntries().FirstOrDefault(e => e.Id == id);
        if (entry == null)
            return $"Error: context id not found: {id}";
        if (!File.Exists(entry.Path))
            return $"Error: context blob missing for {id}: {entry.Path}";

        var content = File.ReadAllText(entry.Path);
        maxChars = Math.Clamp(maxChars, 500, 50000);

        if (!string.IsNullOrWhiteSpace(query))
        {
            var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var snippet = MakeSnippet(content, terms, maxChars);
            return $"[{entry.Id}] {entry.Source} focused on '{query}'\n\n{snippet}";
        }

        if (content.Length <= maxChars)
            return $"[{entry.Id}] {entry.Source}\n\n{content}";

        return $"[{entry.Id}] {entry.Source} ({entry.Length:N0} chars, first {maxChars:N0})\n\n" +
               content[..maxChars] +
               $"\n... ({content.Length - maxChars:N0} more chars; use ctx_read with query to retrieve focused snippets)";
    }

    public static string Stats()
    {
        var entries = LoadEntries().ToList();
        var chars = entries.Sum(e => e.Length);
        return "Context store\n" +
               $"Session: {SessionId}\n" +
               $"Path: {RootPath}\n" +
               $"Entries: {entries.Count:N0}\n" +
               $"Stored chars: {chars:N0}";
    }

    public static IReadOnlyList<ContextEntry> ListEntries()
    {
        return LoadEntries()
            .OrderBy(e => e.CreatedAt, StringComparer.Ordinal)
            .ToList();
    }

    public static bool Delete(string id, out string message)
    {
        var entries = LoadEntries().ToList();
        var entry = entries.FirstOrDefault(e => e.Id == id);
        if (entry == null)
        {
            message = $"Context id not found: {id}";
            return false;
        }

        lock (Lock)
        {
            if (File.Exists(entry.Path))
                File.Delete(entry.Path);

            WriteIndex(entries.Where(e => e.Id != id));
        }

        message = $"Deleted context entry {id}.";
        return true;
    }

    public static int Clear()
    {
        var entries = LoadEntries().ToList();
        lock (Lock)
        {
            foreach (var entry in entries)
            {
                if (File.Exists(entry.Path))
                    File.Delete(entry.Path);
            }

            WriteIndex(Array.Empty<ContextEntry>());
        }

        return entries.Count;
    }

    public static string GetRootPath() => RootPath;

    private static void WriteIndex(IEnumerable<ContextEntry> entries)
    {
        Directory.CreateDirectory(RootPath);
        var lines = entries.Select(entry => JsonSerializer.Serialize(entry, JsonOptions));
        File.WriteAllLines(IndexPath, lines);
    }

    private static IEnumerable<ContextEntry> LoadEntries()
    {
        if (!File.Exists(IndexPath))
            yield break;

        foreach (var line in File.ReadLines(IndexPath))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            ContextEntry? entry;
            try
            {
                entry = JsonSerializer.Deserialize<ContextEntry>(line);
            }
            catch
            {
                continue;
            }

            if (entry != null)
                yield return entry;
        }
    }

    private static ContextSearchHit ScoreEntry(ContextEntry entry, string[] terms)
    {
        if (!File.Exists(entry.Path))
            return new ContextSearchHit(entry.Id, entry.Source, entry.Length, 0, "");

        var content = File.ReadAllText(entry.Path);
        var score = 0;
        foreach (var term in terms)
        {
            var index = 0;
            while ((index = content.IndexOf(term, index, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                score++;
                index += term.Length;
            }
        }

        var snippet = score > 0 ? MakeSnippet(content, terms, 900) : "";
        return new ContextSearchHit(entry.Id, entry.Source, entry.Length, score, snippet);
    }

    private static string FormatStoredResult(ContextEntry entry)
    {
        return $"Stored large context as {entry.Id} from {entry.Source} ({entry.Length:N0} chars).\n" +
               "Use ctx_search to find relevant snippets or ctx_read with this id to retrieve focused content.\n\n" +
               $"Preview:\n{entry.Preview}";
    }

    private static string MakePreview(string content, int maxChars)
    {
        var normalized = content.Replace("\r\n", "\n").Trim();
        if (normalized.Length <= maxChars)
            return normalized;
        return normalized[..maxChars] + $"\n... ({normalized.Length - maxChars:N0} more chars stored)";
    }

    private static string MakeSnippet(string content, string[] terms, int maxChars)
    {
        var firstMatch = terms
            .Select(term => content.IndexOf(term, StringComparison.OrdinalIgnoreCase))
            .Where(i => i >= 0)
            .DefaultIfEmpty(0)
            .Min();

        var start = Math.Max(0, firstMatch - maxChars / 3);
        var length = Math.Min(maxChars, content.Length - start);
        var snippet = content.Substring(start, length).Trim();

        if (start > 0)
            snippet = "... " + snippet;
        if (start + length < content.Length)
            snippet += " ...";

        return snippet;
    }
}
