using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Spectre.Console;
using static sif.agent.ShellCommandParser;

namespace sif.agent;

/// <summary>
/// Result of checking a shell command line against the allowlist. Commands that are merely
/// unknown can be approved for the session or persisted; warnings flag destructive or
/// unanalyzable commands and always need a fresh, per-call approval.
/// </summary>
internal sealed record ShellCommandAssessment(IReadOnlyList<string> UnapprovedCommands, IReadOnlyList<string> Warnings)
{
    public bool RequiresApproval => UnapprovedCommands.Count > 0 || Warnings.Count > 0;

    public string Describe()
    {
        var parts = new List<string>();
        if (UnapprovedCommands.Count > 0)
            parts.Add($"Not in the allowlist: {string.Join(", ", UnapprovedCommands)}.");
        if (Warnings.Count > 0)
            parts.Add($"Flagged: {string.Join("; ", Warnings)}.");
        return string.Join(" ", parts);
    }
}

/// <summary>
/// Determines which shell commands the bash tool is permitted to run and
/// manages session/persisted user approvals. Every command in a line is checked,
/// including those after &amp;&amp;, |, ; and inside substitutions, wrappers such as
/// xargs/env/timeout and nested sh -c scripts.
/// </summary>
internal static partial class ShellCommandPolicy
{
    private static readonly HashSet<string> SessionAllowedShellCommands = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object SessionAllowedShellCommandsLock = new();

    private static readonly HashSet<string> PrefixKeywords = new(StringComparer.Ordinal)
    {
        "if", "then", "else", "elif", "do", "while", "until", "!", "{", "}", "fi", "done"
    };

    private static readonly HashSet<string> HeaderKeywords = new(StringComparer.Ordinal) { "for", "select" };

    private static readonly HashSet<string> UnsupportedKeywords = new(StringComparer.Ordinal)
    {
        "case", "esac", "function", "coproc"
    };

    private static readonly HashSet<string> PrivilegeCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "sudo", "doas", "pkexec", "run0", "su"
    };

    private static readonly HashSet<string> PosixShells = new(StringComparer.OrdinalIgnoreCase)
    {
        "sh", "bash", "zsh", "dash", "ksh"
    };

    private static readonly HashSet<string> PowerShells = new(StringComparer.OrdinalIgnoreCase)
    {
        "pwsh", "powershell"
    };

    private static readonly string[] TrustedBinaryDirectories =
    [
        "/bin", "/sbin", "/usr/bin", "/usr/sbin", "/usr/local/bin", "/usr/local/sbin", "/opt/homebrew/bin"
    ];

    private static readonly HashSet<string> HarmlessDevices = new(StringComparer.Ordinal)
    {
        "/dev/null", "/dev/zero", "/dev/stdout", "/dev/stderr", "/dev/stdin", "/dev/tty"
    };

    private static readonly HashSet<string> PowerStateVerbs = new(StringComparer.OrdinalIgnoreCase)
    {
        "poweroff", "reboot", "halt", "kexec", "soft-reboot", "suspend", "hibernate", "hybrid-sleep",
        "suspend-then-hibernate", "isolate", "rescue", "emergency", "default"
    };

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*(\[[^\]]*\])?\+?=")]
    private static partial Regex AssignmentPattern();

    public static HashSet<string> GetAllowedShellCommands()
    {
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // Shell builtins and keywords
            "cd", "pwd", "echo", "printf", "test", "[", "[[", "true", "false", ":", "read",
            "command", "type", "alias", "export", "set", "unset", "local", "declare", "typeset",
            "readonly", "shift", "exit", "return", "break", "continue", "wait", "trap", "eval",
            "ulimit", "umask", "pushd", "popd", "dirs", "hash", "getopts",
            // Wrappers; the wrapped command is checked separately
            "env", "xargs", "nohup", "timeout", "time", "nice", "stdbuf",
            // Files and text
            "ls", "cat", "grep", "rg", "find", "fd", "head", "tail", "wc", "file", "stat", "du", "df",
            "man", "locate", "which", "dirname", "basename", "realpath", "readlink",
            "diff", "sort", "uniq", "tr", "cut", "sed", "awk", "tree", "less", "more", "strings",
            "tac", "rev", "nl", "column", "expand", "unexpand", "seq", "bc", "expr",
            "mktemp", "chmod", "chown", "mkdir", "rm", "cp", "mv", "ln",
            "touch", "tee", "split", "join", "paste", "comm", "fold",
            "fmt", "pr", "md5sum", "sha1sum", "sha256sum", "sha512sum", "b2sum", "cksum",
            "base64", "xxd", "od", "hexdump", "dd", "jq", "patch", "cmp", "diff3", "sdiff",
            // Archives
            "gzip", "gunzip", "zcat", "tar", "zip", "unzip", "xz", "unxz", "zstd", "bzip2", "bunzip2",
            // System and processes
            "whoami", "id", "groups", "hostname", "uname", "uptime", "date", "printenv",
            "free", "nproc", "sleep", "ps", "pgrep", "kill", "top", "htop", "lsof", "mount", "blkid",
            "tmux", "screen",
            // Network
            "curl", "wget", "ping", "ip", "ifconfig", "netstat", "ss", "dig", "host", "nslookup",
            "traceroute", "ssh", "scp", "rsync",
            // Editors
            "vi", "vim", "nano", "emacs",
            // Development
            "git", "csi", "npm", "npx", "pnpm", "yarn", "bun", "deno", "node",
            "pip", "pip3", "uv", "python", "python3", "ruby", "java", "javac", "mvn", "gradle",
            "gcc", "g++", "clang", "make", "cmake", "cargo", "rustc", "dotnet", "go", "swift",
            "docker", "kubectl", "terraform", "ansible"
        };

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            foreach (var command in new[]
            {
                "systemctl", "journalctl", "lscpu", "lsblk", "lsusb", "lspci", "lsmod", "dmesg", "findmnt"
            })
            {
                allowed.Add(command);
            }
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            foreach (var command in new[]
            {
                "dir", "type", "systeminfo", "findstr", "where", "cls", "ver", "vol", "path", "setx",
                "copy", "xcopy", "robocopy", "move", "del", "erase", "ren", "rename", "rmdir",
                "md", "rd", "attrib", "icacls", "takeown", "fc", "comp", "certutil",
                "tasklist", "taskkill", "sc", "net", "netsh", "ipconfig", "arp", "route",
                "nslookup", "tracert", "powershell", "powershell.exe", "pwsh", "pwsh.exe",
                "Get-ChildItem", "Get-Content", "Select-String", "Get-Location", "Set-Location",
                "Get-Command", "Get-Item", "Get-ItemProperty", "Get-Date", "Get-Process",
                "Stop-Process", "Get-Service", "Start-Service", "Stop-Service", "Restart-Service",
                "New-Item", "Remove-Item", "Copy-Item", "Move-Item", "Rename-Item",
                "Set-Content", "Add-Content", "Out-File", "Measure-Object", "Sort-Object",
                "Where-Object", "ForEach-Object", "Format-Table", "Format-List",
                "Resolve-Path", "Split-Path", "Join-Path", "Test-Path", "Get-FileHash",
                "Expand-Archive", "Compress-Archive", "Invoke-WebRequest", "Invoke-RestMethod",
                "iwr", "irm", "gc", "sc", "gci", "gi", "gp", "pwd", "sls", "sort",
                "measure", "ft", "fl", "rvpa", "sp", "ni", "ri", "mi", "ren", "cp", "mv", "rm"
            })
            {
                allowed.Add(command);
            }
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            foreach (var command in new[]
            {
                "sw_vers", "system_profiler", "sysctl", "scutil", "networksetup",
                "ifconfig", "route", "netstat", "lsof", "launchctl", "defaults",
                "plutil", "osascript", "open", "say", "mdfind", "mdls", "diskutil",
                "hdiutil", "ioreg", "pmset", "softwareupdate", "xcode-select",
                "xcrun", "clang", "clang++", "codesign", "security", "dscl",
                "dscacheutil", "spctl", "csrutil", "pbcopy", "pbpaste", "screencapture",
                "caffeinate", "brew"
            })
            {
                allowed.Add(command);
            }
        }

        return allowed;
    }

    /// <summary>Checks a command line against the built-in, session and configured allowlists.</summary>
    public static ShellCommandAssessment Assess(string command)
    {
        var builtIn = GetAllowedShellCommands();
        string[]? configured = null;
        return Assess(command, name =>
        {
            if (MatchesAllowList(name, builtIn))
                return true;

            lock (SessionAllowedShellCommandsLock)
            {
                if (MatchesAllowList(name, SessionAllowedShellCommands))
                    return true;
            }

            configured ??= AgentConfig.Load().ShellAllowedCommands ?? [];
            return MatchesAllowList(name, configured);
        });
    }

    internal static ShellCommandAssessment Assess(string command, Func<string, bool> isAllowed, bool? posix = null)
    {
        var assessor = new Assessor(isAllowed);
        assessor.AssessScript(command, posix ?? !RuntimeInformation.IsOSPlatform(OSPlatform.Windows), depth: 0);
        return new ShellCommandAssessment(
            assessor.Unapproved.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            assessor.Warnings.Distinct(StringComparer.Ordinal).ToList());
    }

    /// <summary>
    /// Bare names match case-insensitively, with or without an extension (git.exe). A name with a
    /// directory only matches by its base name when it lives in a system binary directory, so a
    /// script called ./ls cannot borrow the approval of ls.
    /// </summary>
    internal static bool MatchesAllowList(string name, IEnumerable<string> allowList)
    {
        bool Contains(string candidate) =>
            allowList.Any(item => string.Equals(item, candidate, StringComparison.OrdinalIgnoreCase));

        if (string.IsNullOrWhiteSpace(name))
            return false;

        if (Contains(name))
            return true;

        if (HasDirectory(name))
        {
            var directory = Path.GetDirectoryName(name)?.Replace('\\', '/');
            if (directory is null || !TrustedBinaryDirectories.Contains(directory, StringComparer.Ordinal))
                return false;

            name = Path.GetFileName(name);
            if (Contains(name))
                return true;
        }

        var extensionless = Path.GetFileNameWithoutExtension(name);
        return extensionless != name && Contains(extensionless);
    }

    public static bool PromptForApproval(ShellCommandAssessment assessment, string command)
    {
        if (Console.IsInputRedirected || Console.IsOutputRedirected)
            return false;

        var displayCommand = command.Length > 500 ? command[..500] + "..." : command;
        AnsiConsole.WriteLine();
        if (assessment.UnapprovedCommands.Count > 0)
            AnsiConsole.MarkupLine($"[yellow]Not in the allowlist: {string.Join(", ", assessment.UnapprovedCommands).EscapeMarkup()}[/]");
        foreach (var warning in assessment.Warnings)
            AnsiConsole.MarkupLine($"[red]Warning: {warning.EscapeMarkup()}[/]");
        AnsiConsole.MarkupLine("[dim]Full command:[/]");
        AnsiConsole.MarkupLine(displayCommand.EscapeMarkup());

        // Flagged commands are approved one call at a time; remembering them would whitelist the
        // harmless use of the same command rather than this particular dangerous one.
        var choices = assessment.Warnings.Count > 0
            ? new[] { "Allow once", "Deny" }
            : new[] { "Allow once", "Allow for this session", "Allow always", "Deny" };

        var choice = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("Allow this command?")
                .AddChoices(choices));

        if (choice == "Deny")
            return false;

        foreach (var name in assessment.UnapprovedCommands)
        {
            if (choice is "Allow for this session" or "Allow always")
                AddSessionAllowedShellCommand(name);
            if (choice == "Allow always")
                AddPersistedAllowedShellCommand(name);
        }

        return true;
    }

    private static bool HasDirectory(string name) => name.Contains('/') || name.Contains('\\');

    private static string BaseName(string name)
    {
        var fileName = Path.GetFileName(name.Replace('\\', '/'));
        return fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? fileName[..^4] : fileName;
    }

    private static void AddSessionAllowedShellCommand(string command)
    {
        lock (SessionAllowedShellCommandsLock)
        {
            SessionAllowedShellCommands.Add(command);
        }
    }

    private static void AddPersistedAllowedShellCommand(string command)
    {
        var config = AgentConfig.Load();
        var commands = new HashSet<string>(config.ShellAllowedCommands ?? [], StringComparer.OrdinalIgnoreCase)
        {
            command
        };

        config.ShellAllowedCommands = commands
            .OrderBy(item => item, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        config.Save();
    }

    private sealed class Assessor(Func<string, bool> isAllowed)
    {
        private const int MaxDepth = 8;

        public List<string> Unapproved { get; } = [];
        public List<string> Warnings { get; } = [];

        public void AssessScript(string script, bool posix, int depth)
        {
            if (depth > MaxDepth)
            {
                Warnings.Add("nested shell scripts are too deep to analyze");
                return;
            }

            var parsed = Parse(script, posix);
            if (parsed.Unsupported is not null)
                Warnings.Add($"could not fully analyze the command ({parsed.Unsupported})");

            foreach (var command in parsed.Commands)
            {
                CheckRedirects(command.Redirects);
                AssessWords(command.Words, posix, depth);
            }
        }

        private void AssessWords(IReadOnlyList<ShellWord> words, bool posix, int depth)
        {
            var index = 0;
            while (index < words.Count)
            {
                var text = words[index].Text;
                if (PrefixKeywords.Contains(text) || (posix && AssignmentPattern().IsMatch(text)))
                {
                    index++;
                    continue;
                }

                // PowerShell assignment: $x = Get-Thing
                if (!posix && text.StartsWith('$') && index + 1 < words.Count && words[index + 1].Text.EndsWith('='))
                {
                    index += 2;
                    continue;
                }

                break;
            }

            if (index >= words.Count)
                return;

            var name = words[index].Text;
            var args = words.Skip(index + 1).ToList();
            var baseName = BaseName(name);

            if (HeaderKeywords.Contains(name))
                return;

            if (UnsupportedKeywords.Contains(name))
            {
                Warnings.Add($"'{name}' blocks are not analyzed");
                return;
            }

            if (words[index].Dynamic)
            {
                Warnings.Add($"the command name '{name}' is computed at run time");
                return;
            }

            if (PrivilegeCommands.Contains(baseName))
            {
                Warnings.Add($"'{name}' runs commands with elevated privileges");
                if (baseName != "su")
                    AssessWords(SkipOptions(args, ["-u", "-g", "-C", "-h", "-p", "-U", "-r", "-t", "-D"]), posix, depth);
                return;
            }

            if (PosixShells.Contains(baseName) && TryGetShellScript(args, out var script, out var dynamic))
            {
                if (dynamic)
                    Warnings.Add($"'{name} -c' runs a script that is computed at run time");
                else
                    AssessScript(script, posix: true, depth + 1);
                return;
            }

            if (PowerShells.Contains(baseName) && TryGetPowerShellScript(args, out script, out var encoded))
            {
                if (encoded)
                    Warnings.Add($"'{name}' runs an encoded command that cannot be inspected");
                else
                    AssessScript(script, posix: false, depth + 1);
                return;
            }

            if (!isAllowed(name))
                Unapproved.Add(name);

            CheckDanger(baseName, args);
            AssessWrapped(baseName, args, posix, depth);
        }

        /// <summary>Checks the command run by wrappers such as env, xargs, timeout and find -exec.</summary>
        private void AssessWrapped(string name, List<ShellWord> args, bool posix, int depth)
        {
            switch (name)
            {
                case "env":
                    var envArgs = SkipOptions(args, ["-u", "--unset", "-C", "--chdir", "-S", "--split-string"]);
                    AssessWords(envArgs.SkipWhile(arg => arg.Text.Contains('=')).ToList(), posix, depth);
                    break;
                case "command":
                    if (!args.Any(arg => arg.Text is "-v" or "-V"))
                        AssessWords(SkipOptions(args, []), posix, depth);
                    break;
                case "exec":
                    AssessWords(SkipOptions(args, ["-a"]), posix, depth);
                    break;
                case "nohup":
                    AssessWords(args, posix, depth);
                    break;
                case "time":
                    AssessWords(SkipOptions(args, ["-f", "--format", "-o", "--output"]), posix, depth);
                    break;
                case "nice":
                    AssessWords(SkipOptions(args, ["-n", "--adjustment"]), posix, depth);
                    break;
                case "stdbuf":
                    AssessWords(SkipOptions(args, ["-i", "-o", "-e"]), posix, depth);
                    break;
                case "timeout":
                    AssessWords(SkipOptions(args, ["-s", "--signal", "-k", "--kill-after"]).Skip(1).ToList(), posix, depth);
                    break;
                case "xargs":
                    AssessWords(SkipOptions(args, ["-I", "-n", "-P", "-d", "-L", "-s", "-a", "-E",
                        "--delimiter", "--max-args", "--max-procs", "--arg-file", "--max-lines", "--max-chars"]), posix, depth);
                    break;
                case "find":
                    AssessFindExec(args, posix, depth);
                    break;
                case "eval":
                    if (args.Any(arg => arg.Dynamic))
                        Warnings.Add("'eval' runs text that is computed at run time");
                    else
                        AssessScript(string.Join(' ', args.Select(arg => arg.Text)), posix, depth + 1);
                    break;
                case "trap":
                    if (args.Count > 0 && !args[0].Text.StartsWith('-'))
                    {
                        if (args[0].Dynamic)
                            Warnings.Add("'trap' runs text that is computed at run time");
                        else
                            AssessScript(args[0].Text, posix, depth + 1);
                    }
                    break;
            }
        }

        private void AssessFindExec(List<ShellWord> args, bool posix, int depth)
        {
            for (var i = 0; i < args.Count; i++)
            {
                if (args[i].Text is not ("-exec" or "-execdir" or "-ok" or "-okdir"))
                    continue;

                var end = i + 1;
                while (end < args.Count && args[end].Text is not (";" or "+"))
                    end++;

                AssessWords(args.GetRange(i + 1, end - i - 1), posix, depth);
                i = end;
            }
        }

        private void CheckDanger(string name, List<ShellWord> args)
        {
            var texts = args.Select(arg => arg.Text).ToList();
            switch (name)
            {
                case "rm":
                    if (texts.Contains("--no-preserve-root"))
                        Warnings.Add("'rm --no-preserve-root' can delete the whole filesystem");
                    if (HasFlag(texts, 'r', "--recursive") || HasFlag(texts, 'R', "--recursive"))
                    {
                        foreach (var target in Operands(texts).Where(target => IsCriticalPath(target, includeWorkingDirectory: true)))
                            Warnings.Add($"'rm' would recursively delete '{target}'");
                    }
                    break;
                case "chmod" or "chown" or "chgrp":
                    if (HasFlag(texts, 'R', "--recursive"))
                    {
                        foreach (var target in Operands(texts).Where(target => IsCriticalPath(target, includeWorkingDirectory: false)))
                            Warnings.Add($"'{name} -R' would change '{target}' recursively");
                    }
                    break;
                case "find":
                    if (texts.Contains("-delete"))
                    {
                        foreach (var start in texts.TakeWhile(text => !text.StartsWith('-') && text is not ("(" or "!"))
                                     .Where(start => IsCriticalPath(start, includeWorkingDirectory: false)))
                            Warnings.Add($"'find -delete' would delete files under '{start}'");
                    }
                    break;
                case "dd":
                    foreach (var text in texts.Where(text => text.StartsWith("of=", StringComparison.Ordinal)))
                    {
                        if (IsDangerousDevice(text[3..]))
                            Warnings.Add($"'dd' would write directly to '{text[3..]}'");
                    }
                    break;
                case "tee":
                    foreach (var target in Operands(texts).Where(IsDangerousDevice))
                        Warnings.Add($"'tee' would write directly to '{target}'");
                    break;
                case "kill":
                    if (texts.Skip(1).Contains("-1"))
                        Warnings.Add("'kill -1' signals every process you own");
                    break;
                case "systemctl":
                    var verb = Operands(texts).FirstOrDefault();
                    if (verb is not null && PowerStateVerbs.Contains(verb))
                        Warnings.Add($"'systemctl {verb}' changes the system power state or boot target");
                    break;
                case "git":
                    CheckGitDanger(texts);
                    break;
            }
        }

        private void CheckGitDanger(List<string> texts)
        {
            // Skip global options, some of which take a value (git -C dir status).
            var i = 0;
            while (i < texts.Count && texts[i].StartsWith('-'))
                i += texts[i] is "-C" or "-c" or "--git-dir" or "--work-tree" or "--namespace" ? 2 : 1;

            if (i >= texts.Count)
                return;

            var subcommand = texts[i];
            var rest = texts.Skip(i + 1).ToList();
            switch (subcommand)
            {
                case "push" when rest.Contains("--mirror") || HasFlag(rest, 'f', "--force") ||
                                 Operands(rest).Any(refspec => refspec.StartsWith('+')):
                    Warnings.Add("'git push --force' rewrites history on the remote");
                    break;
                case "reset" when rest.Contains("--hard"):
                    Warnings.Add("'git reset --hard' discards uncommitted changes");
                    break;
                case "clean" when HasFlag(rest, 'f', "--force"):
                    Warnings.Add("'git clean -f' permanently deletes untracked files");
                    break;
            }
        }

        private void CheckRedirects(IEnumerable<ShellRedirect> redirects)
        {
            foreach (var redirect in redirects)
            {
                if (redirect.Operator.Contains('>') && IsDangerousDevice(redirect.Target))
                    Warnings.Add($"output is redirected directly to '{redirect.Target}'");
            }
        }

        private static bool TryGetShellScript(List<ShellWord> args, out string script, out bool dynamic)
        {
            script = "";
            dynamic = false;
            var hasCommandFlag = false;
            for (var i = 0; i < args.Count; i++)
            {
                var text = args[i].Text;
                if (text == "--")
                    continue;

                if (text.StartsWith('-') || text.StartsWith('+'))
                {
                    if (text is "-o" or "+o" or "-O" or "+O" or "--rcfile" or "--init-file")
                        i++;
                    else if (text.StartsWith('-') && !text.StartsWith("--", StringComparison.Ordinal) && text.Contains('c'))
                        hasCommandFlag = true;
                    continue;
                }

                if (!hasCommandFlag)
                    return false;

                script = text;
                dynamic = args[i].Dynamic;
                return true;
            }

            return false;
        }

        private static bool TryGetPowerShellScript(List<ShellWord> args, out string script, out bool encoded)
        {
            script = "";
            encoded = false;
            for (var i = 0; i < args.Count; i++)
            {
                var option = args[i].Text.ToLowerInvariant();
                if (option is "-encodedcommand" or "-enc" or "-e" or "-ec")
                {
                    encoded = true;
                    return true;
                }

                if (option is "-command" or "-c")
                {
                    script = string.Join(' ', args.Skip(i + 1).Select(arg => arg.Text));
                    return true;
                }
            }

            return false;
        }

        /// <summary>Drops leading options (and the values of <paramref name="optionsWithValue"/>) up to the wrapped command.</summary>
        private static List<ShellWord> SkipOptions(List<ShellWord> args, string[] optionsWithValue)
        {
            var i = 0;
            while (i < args.Count && args[i].Text.StartsWith('-') && args[i].Text.Length > 1)
            {
                if (args[i].Text == "--")
                    return args.Skip(i + 1).ToList();

                i += optionsWithValue.Contains(args[i].Text, StringComparer.Ordinal) ? 2 : 1;
            }

            return args.Skip(i).ToList();
        }

        private static bool HasFlag(List<string> texts, char shortFlag, string longFlag) =>
            texts.Any(text => text == longFlag ||
                (text.Length > 1 && text[0] == '-' && text[1] != '-' && text.Contains(shortFlag)));

        private static IEnumerable<string> Operands(List<string> texts) =>
            texts.Where(text => !text.StartsWith('-'));

        private static bool IsDangerousDevice(string path) =>
            path.StartsWith("/dev/", StringComparison.Ordinal) &&
            !HarmlessDevices.Contains(path) &&
            !path.StartsWith("/dev/fd/", StringComparison.Ordinal) &&
            !path.StartsWith("/dev/shm/", StringComparison.Ordinal) &&
            !path.StartsWith("/dev/pts/", StringComparison.Ordinal);

        /// <summary>
        /// The filesystem root, top-level system directories, the home directory, and (for rm) the
        /// working directory or its parent as a whole.
        /// </summary>
        private static bool IsCriticalPath(string path, bool includeWorkingDirectory)
        {
            var normalized = path.Trim();
            foreach (var suffix in new[] { "/*", "/.*", "/" })
            {
                while (normalized.Length > 1 && normalized.EndsWith(suffix, StringComparison.Ordinal))
                    normalized = normalized[..^suffix.Length];
            }

            if (normalized is "" or "/" or "/*" or "~" or "$HOME" or "${HOME}" or ".." or "../.." or "*" or ".*")
                return normalized is not ("*" or ".*") || includeWorkingDirectory;

            if (includeWorkingDirectory && normalized is "." or "./*")
                return true;

            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).TrimEnd('/');
            if (home.Length > 0 && normalized == home)
                return true;

            if (normalized.StartsWith('/'))
            {
                var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
                return segments.Length == 1 || (segments.Length == 2 && segments[0] is "home" or "Users");
            }

            return false;
        }
    }
}
