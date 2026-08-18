using System.Runtime.InteropServices;

namespace sif.agent.Services;

internal static class ExecutableLocator
{
    public static string? Find(IEnumerable<string> names)
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        var pathExts = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.BAT;.CMD;.COM")
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [""];

        foreach (var name in names)
        {
            if (Path.IsPathRooted(name) && File.Exists(name))
                return name;

            foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                foreach (var ext in pathExts)
                {
                    var candidate = Path.Combine(dir, name);
                    if (!candidate.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                        candidate += ext;
                    if (File.Exists(candidate))
                        return candidate;
                }
            }
        }

        return null;
    }
}
