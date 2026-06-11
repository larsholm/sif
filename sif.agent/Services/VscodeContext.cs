using System.Text;
using System.Text.Json;
using Spectre.Console;
using sif.agent.Services.Tools;

namespace sif.agent;

internal static class VscodeContext
{
    private const int MaxInlineTextChars = 24000;
    private const int MaxLineChars = 4000;

    private static string? _lastEditorContext = null;

    public static string? FilePath => ReadSnapshot().FilePath ?? NormalizePath(GetFirstEnv("SIF_VSCODE_FILE", "VSCODE_ACTIVE_FILE"));
    public static string? Line => ReadSnapshot().Line ?? GetFirstEnv("SIF_VSCODE_LINE", "SIF_VSCODE_SELECTION_START_LINE", "VSCODE_ACTIVE_LINE");
    public static string? Column => ReadSnapshot().Column ?? GetFirstEnv("SIF_VSCODE_COLUMN", "SIF_VSCODE_SELECTION_START_COLUMN", "VSCODE_ACTIVE_COLUMN");
    public static string? SelectedText => ReadSnapshot().SelectedText
        ?? DecodeText(GetFirstEnv("SIF_VSCODE_SELECTED_TEXT_B64"), isBase64: true)
        ?? DecodeText(GetFirstEnv("SIF_VSCODE_SELECTED_TEXT", "VSCODE_SELECTED_TEXT"), isBase64: false);

    public static bool IsRunningInVscodeTerminal()
    {
        return string.Equals(Environment.GetEnvironmentVariable("TERM_PROGRAM"), "vscode", StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("VSCODE_IPC_HOOK_CLI"))
            || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("VSCODE_PID"))
            || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SIF_VSCODE_CONTEXT_FILE"));
    }

    public static string AppendToUserMessage(string message)
    {
        if (!IsRunningInVscodeTerminal())
            return message;

        var block = BuildMessageBlock();
        if (string.IsNullOrWhiteSpace(block))
            return message;

        // Only include editor context if it changed since the last turn
        if (block == _lastEditorContext)
            return message;

        _lastEditorContext = block;
        return message + "\n\n" + block;
    }

    public static string GetDisplayValue(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "[dim]not provided[/]" : value.EscapeMarkup();
    }

    private static string? BuildMessageBlock()
    {
        var snapshot = ReadSnapshot();
        var file = snapshot.FilePath ?? NormalizePath(GetFirstEnv("SIF_VSCODE_FILE", "VSCODE_ACTIVE_FILE"));
        var line = snapshot.Line ?? GetFirstEnv("SIF_VSCODE_LINE", "SIF_VSCODE_SELECTION_START_LINE", "VSCODE_ACTIVE_LINE");
        var column = snapshot.Column ?? GetFirstEnv("SIF_VSCODE_COLUMN", "SIF_VSCODE_SELECTION_START_COLUMN", "VSCODE_ACTIVE_COLUMN");
        var selectedText = snapshot.SelectedText
            ?? DecodeText(GetFirstEnv("SIF_VSCODE_SELECTED_TEXT_B64"), isBase64: true)
            ?? DecodeText(GetFirstEnv("SIF_VSCODE_SELECTED_TEXT", "VSCODE_SELECTED_TEXT"), isBase64: false);
        var currentLine = TryReadLine(file, line);

        if (string.IsNullOrWhiteSpace(file) &&
            string.IsNullOrWhiteSpace(line) &&
            string.IsNullOrWhiteSpace(column) &&
            string.IsNullOrWhiteSpace(selectedText))
            return null;

        var sb = new StringBuilder();
        sb.AppendLine("<editor_context source=\"vscode\">");
        if (!string.IsNullOrWhiteSpace(file))
            sb.AppendLine($"Open file: {file}");
        if (!string.IsNullOrWhiteSpace(line) || !string.IsNullOrWhiteSpace(column))
            sb.AppendLine($"Cursor: line {ValueOrUnknown(line)}, column {ValueOrUnknown(column)}");
        if (!string.IsNullOrWhiteSpace(currentLine))
            sb.AppendLine($"Current line: {currentLine}");
        if (!string.IsNullOrWhiteSpace(selectedText))
        {
            sb.AppendLine("Selected text:");
            sb.AppendLine(TrimText(selectedText, MaxInlineTextChars));
        }
        sb.AppendLine("</editor_context>");

        var roslynContext = RoslynTools.BuildAmbientContext(file, line);
        if (!string.IsNullOrWhiteSpace(roslynContext))
        {
            sb.AppendLine();
            sb.AppendLine(roslynContext);
        }

        return sb.ToString().TrimEnd();
    }

    private static string? TryReadLine(string? file, string? line)
    {
        if (string.IsNullOrWhiteSpace(file) || !int.TryParse(line, out var lineNumber) || lineNumber <= 0)
            return null;

        try
        {
            var path = Path.IsPathRooted(file) ? file : Path.GetFullPath(file, Environment.CurrentDirectory);
            if (!File.Exists(path))
                return null;

            var current = 1;
            foreach (var text in File.ReadLines(path))
            {
                if (current == lineNumber)
                    return TrimText(text, MaxLineChars);
                current++;
            }
        }
        catch
        {
            return null;
        }

        return null;
    }

    private static EditorSnapshot ReadSnapshot()
    {
        var path = Environment.GetEnvironmentVariable("SIF_VSCODE_CONTEXT_FILE");
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return EditorSnapshot.Empty;

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;

            // Ignore snapshots from another VS Code window/project: the context
            // file may be shared, so only trust it when its workspace overlaps
            // with the directory this agent is running in.
            var workspaceFolder = NormalizePath(GetJsonString(root, "workspaceFolder"));
            if (!string.IsNullOrWhiteSpace(workspaceFolder)
                && !PathsOverlap(workspaceFolder, Environment.CurrentDirectory))
                return EditorSnapshot.Empty;

            return new EditorSnapshot(
                NormalizePath(GetJsonString(root, "file")),
                GetJsonString(root, "line"),
                GetJsonString(root, "column"),
                GetJsonString(root, "selectedText"));
        }
        catch
        {
            return EditorSnapshot.Empty;
        }
    }

    private static bool PathsOverlap(string a, string b)
    {
        return IsWithinOrEqual(a, b) || IsWithinOrEqual(b, a);
    }

    private static bool IsWithinOrEqual(string candidate, string root)
    {
        try
        {
            var candidateFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
            var rootFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

            return string.Equals(candidateFull, rootFull, comparison)
                || candidateFull.StartsWith(rootFull + Path.DirectorySeparatorChar, comparison);
        }
        catch
        {
            return false;
        }
    }

    private static string? GetJsonString(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null
        };
    }

    private static string ValueOrUnknown(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "unknown" : value;
    }

    private static string? GetFirstEnv(params string[] names)
    {
        foreach (var name in names)
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return null;
    }

    private static string? DecodeText(string? value, bool isBase64)
    {
        if (string.IsNullOrEmpty(value))
            return null;

        if (!isBase64)
            return value.Replace("\\r\\n", "\n").Replace("\\n", "\n");

        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(value));
        }
        catch
        {
            return null;
        }
    }

    private static string? NormalizePath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.IsFile)
            return uri.LocalPath;

        return value;
    }

    private static string TrimText(string value, int maxChars)
    {
        if (value.Length <= maxChars)
            return value;

        return value[..maxChars] + $"\n...[truncated {value.Length - maxChars:N0} chars]";
    }

    private sealed record EditorSnapshot(string? FilePath, string? Line, string? Column, string? SelectedText)
    {
        public static EditorSnapshot Empty { get; } = new(null, null, null, null);
    }
}
