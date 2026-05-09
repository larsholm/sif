using System.Text;

namespace picoNET.agent;

internal sealed record SkillFile(string Name, string Path, string Content);

internal static class SkillStore
{
    private const string SkillsDirName = "skills";
    private const string PicoDirName = ".pico";
    private const string SkillFileName = "SKILL.md";

    public static IReadOnlyList<SkillFile> Load()
    {
        var files = new List<SkillFile>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var root in GetSkillRoots())
        {
            foreach (var path in EnumerateSkillFiles(root))
            {
                var fullPath = Path.GetFullPath(path);
                if (!seen.Add(fullPath))
                    continue;

                try
                {
                    var content = File.ReadAllText(fullPath).Trim();
                    if (content.Length == 0)
                        continue;

                    files.Add(new SkillFile(GetSkillName(fullPath), fullPath, content));
                }
                catch
                {
                    // Ignore unreadable skill files so a single bad file does not block startup.
                }
            }
        }

        return files
            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static string BuildSystemPrompt(string? basePrompt, IReadOnlyList<SkillFile> skills)
    {
        var sb = new StringBuilder();

        if (!string.IsNullOrWhiteSpace(basePrompt))
            sb.AppendLine(basePrompt.Trim());

        if (skills.Count == 0)
            return sb.ToString().TrimEnd();

        if (sb.Length > 0)
            sb.AppendLine();

        sb.AppendLine("Additional skill files are available. Follow them when their description or instructions match the user's request.");
        sb.AppendLine();

        foreach (var skill in skills)
        {
            sb.AppendLine($"<skill name=\"{EscapeAttribute(skill.Name)}\" path=\"{EscapeAttribute(skill.Path)}\">");
            sb.AppendLine(skill.Content);
            sb.AppendLine("</skill>");
            sb.AppendLine();
        }

        return sb.ToString().TrimEnd();
    }

    private static IEnumerable<string> GetSkillRoots()
    {
        foreach (var projectRoot in GetProjectSkillRoots(Environment.CurrentDirectory))
            yield return projectRoot;

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(home))
            yield return Path.Combine(home, PicoDirName, SkillsDirName);
    }

    private static IEnumerable<string> GetProjectSkillRoots(string startDirectory)
    {
        var current = new DirectoryInfo(startDirectory);
        while (current != null)
        {
            yield return Path.Combine(current.FullName, PicoDirName, SkillsDirName);
            current = current.Parent;
        }
    }

    private static IEnumerable<string> EnumerateSkillFiles(string root)
    {
        if (!Directory.Exists(root))
            yield break;

        foreach (var file in Directory.EnumerateFiles(root, "*.md", SearchOption.TopDirectoryOnly))
            yield return file;

        foreach (var file in Directory.EnumerateFiles(root, SkillFileName, SearchOption.AllDirectories))
            yield return file;
    }

    private static string GetSkillName(string path)
    {
        var fileName = Path.GetFileName(path);
        if (fileName.Equals(SkillFileName, StringComparison.OrdinalIgnoreCase))
            return Path.GetFileName(Path.GetDirectoryName(path)!) ?? Path.GetFileNameWithoutExtension(path);

        return Path.GetFileNameWithoutExtension(path);
    }

    private static string EscapeAttribute(string value)
    {
        return value
            .Replace("&", "&amp;")
            .Replace("\"", "&quot;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;");
    }
}
