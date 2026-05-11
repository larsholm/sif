using System.Text;
using System.Text.Json;

namespace sif.agent;

/// <summary>
/// Persists error details to a log file that survives /clear.
/// The assistant can reference this via the /debug command.
/// </summary>
internal static class DebugLog
{
    private const string LogFileName = "errors.jsonl";
    private static readonly object Lock = new();
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    internal static string LogPath
    {
        get
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var dir = Path.Combine(home, ".sif", "debug");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, LogFileName);
        }
    }

    /// <summary>
    /// Save a detailed error record and return the file path.
    /// </summary>
    public static string Save(string source, Exception ex, string? extraContext = null)
    {
        var record = new
        {
            timestamp = DateTimeOffset.UtcNow.ToString("O"),
            source,
            type = ex.GetType().Name,
            message = ex.Message,
            stackTrace = ex.StackTrace ?? "",
            inner = FormatInnerEx(ex),
            context = extraContext ?? "",
        };

        lock (Lock)
        {
            File.AppendAllText(LogPath, JsonSerializer.Serialize(record, JsonOptions) + Environment.NewLine);
        }

        return LogPath;
    }

    /// <summary>
    /// Return the last N error records as formatted text.
    /// </summary>
    public static string Recent(int count = 10)
    {
        if (!File.Exists(LogPath) || new FileInfo(LogPath).Length == 0)
            return "No recent errors.";

        var lines = File.ReadAllLines(LogPath);
        var recent = lines.Skip(Math.Max(0, lines.Length - count)).ToArray();

        var sb = new StringBuilder();
        sb.AppendLine("Recent errors:");

        foreach (var line in recent)
        {
            try
            {
                using var doc = JsonDocument.Parse(line);
                var r = doc.RootElement;
                var ts = r.TryGetProperty("timestamp", out var tsProp) ? tsProp.GetString() : "?";
                var src = r.TryGetProperty("source", out var srcProp) ? srcProp.GetString() : "?";
                var type = r.TryGetProperty("type", out var typeProp) ? typeProp.GetString() : "?";
                var msg = r.TryGetProperty("message", out var msgProp) ? msgProp.GetString() : "?";

                sb.AppendLine($"\n--- [{ts}] {src} ({type}) ---");
                sb.AppendLine(msg);

                if (r.TryGetProperty("context", out var ctxProp) && !string.IsNullOrEmpty(ctxProp.GetString()))
                    sb.AppendLine($"Context: {ctxProp.GetString()}");

                if (r.TryGetProperty("inner", out var innerProp) && !string.IsNullOrEmpty(innerProp.GetString()))
                    sb.AppendLine($"Inner: {innerProp.GetString()}");

                if (r.TryGetProperty("stackTrace", out var stProp) && !string.IsNullOrEmpty(stProp.GetString()))
                {
                    var st = stProp.GetString()!;
                    var firstLine = st.Split('\n')[0];
                    sb.AppendLine($"Stack: {firstLine}");
                }
            }
            catch
            {
                sb.AppendLine($"[unparseable JSON: {line.Substring(0, Math.Min(100, line.Length))}...]");
            }
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Return the most recent single error record as formatted text.
    /// </summary>
    public static string Latest()
    {
        if (!File.Exists(LogPath) || new FileInfo(LogPath).Length == 0)
            return "No errors logged.";

        var lines = File.ReadAllLines(LogPath);
        var last = lines[^1];

        try
        {
            using var doc = JsonDocument.Parse(last);
            var r = doc.RootElement;
            var ts = r.TryGetProperty("timestamp", out var tsProp) ? tsProp.GetString() : "?";
            var src = r.TryGetProperty("source", out var srcProp) ? srcProp.GetString() : "?";
            var type = r.TryGetProperty("type", out var typeProp) ? typeProp.GetString() : "?";
            var msg = r.TryGetProperty("message", out var msgProp) ? msgProp.GetString() : "?";

            var sb = new StringBuilder();
            sb.AppendLine($"Latest error [{ts}] {src} ({type}):");
            sb.AppendLine(msg);

            if (r.TryGetProperty("context", out var ctxProp) && !string.IsNullOrEmpty(ctxProp.GetString()))
                sb.AppendLine($"Context: {ctxProp.GetString()}");

            if (r.TryGetProperty("inner", out var innerProp) && !string.IsNullOrEmpty(innerProp.GetString()))
                sb.AppendLine($"Inner: {innerProp.GetString()}");

            if (r.TryGetProperty("stackTrace", out var stProp) && !string.IsNullOrEmpty(stProp.GetString()))
                sb.AppendLine(stProp.GetString());

            return sb.ToString().TrimEnd();
        }
        catch
        {
            return $"Error reading log: {last.Substring(0, Math.Min(100, last.Length))}";
        }
    }

    private static string FormatInnerEx(Exception ex)
    {
        var inner = ex.InnerException;
        if (inner == null) return "";

        var parts = new List<string>();
        while (inner != null)
        {
            parts.Add($"{inner.GetType().Name}: {inner.Message}");
            inner = inner.InnerException;
        }
        return string.Join(" -> ", parts);
    }
}
