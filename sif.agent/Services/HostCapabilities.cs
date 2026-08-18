namespace sif.agent.Services;

internal static class HostCapabilities
{
    private static readonly (string Label, string[] Commands)[] KnownCapabilities =
    [
        ("dotnet", ["dotnet"]),
        ("Python", ["python", "python3", "py"]),
        ("Node.js", ["node", "nodejs"]),
        ("git", ["git"]),
        ("gh", ["gh"]),
        ("hf", ["hf", "huggingface-cli"]),
        ("ripgrep", ["rg"]),
        ("jq", ["jq"]),
        ("Docker", ["docker"]),
        ("Podman", ["podman"]),
        ("Go", ["go"]),
        ("Rust", ["cargo", "rustc"]),
        ("Java", ["java", "javac"]),
        ("C/C++", ["gcc", "clang", "cl"]),
        ("CMake", ["cmake"]),
    ];

    public static string? BuildSummary(Func<string, bool>? commandExists = null)
    {
        commandExists ??= command => ExecutableLocator.Find([command]) is not null;

        var available = KnownCapabilities
            .Where(capability => capability.Commands.Any(commandExists))
            .Select(capability => capability.Label)
            .ToArray();

        return available.Length == 0
            ? null
            : $"Host system has: {string.Join(", ", available)}.";
    }

    public static string BuildShellSummary(bool? isWindows = null)
    {
        var shell = (isWindows ?? OperatingSystem.IsWindows()) ? "PowerShell" : "Bash";
        return $"Shell commands run in {shell}.";
    }
}
