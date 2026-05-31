using System.Runtime.InteropServices;
using Spectre.Console;

namespace sif.agent;

/// <summary>
/// Determines which shell commands the bash tool is permitted to run and
/// manages session/persisted user approvals.
/// </summary>
internal static class ShellCommandPolicy
{
    private static readonly HashSet<string> SessionAllowedShellCommands = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object SessionAllowedShellCommandsLock = new();

    public static string GetFirstCommandWord(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return "";

        // Split by ';' to handle compound commands like `var=$(cmd); real_cmd`.
        // Within each part, track parenthesis depth to skip subshell values.
        foreach (var part in command.Split(';'))
        {
            var words = part.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            var depth = 0;

            foreach (var word in words)
            {
                if (depth > 0)
                {
                    foreach (var ch in word)
                    {
                        if (ch == '(') depth++;
                        else if (ch == ')') depth--;
                    }
                    continue;
                }

                // Skip shell variable assignments like VAR=value or VAR=$(subshell)
                var eqIdx = word.IndexOf('=');
                if (eqIdx > 0 && word[..eqIdx].All(c => char.IsLetterOrDigit(c) || c == '_'))
                {
                    foreach (var ch in word[(eqIdx + 1)..])
                    {
                        if (ch == '(') depth++;
                        else if (ch == ')') depth--;
                    }
                    if (depth < 0) depth = 0;
                    continue;
                }

                if (word[0] is '"' or '\'')
                {
                    var quote = word[0];
                    var end = word.IndexOf(quote, 1);
                    return end > 1 ? word[1..end] : word.Trim(quote);
                }

                return word;
            }
        }

        return "";
    }

    public static HashSet<string> GetAllowedShellCommands()
    {
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "#", "ls", "cat", "grep", "find", "pwd", "cd", "csi", "head", "tail", "wc", "whoami",
            "hostname", "date", "env", "echo", "file", "stat", "du", "df",
            "man", "locate", "which", "dirname", "basename", "realpath",
            "diff", "sort", "uniq", "tr", "cut", "sed", "awk", "gzip",
            "gunzip", "tar", "zip", "unzip", "tree", "less", "more",
            "strings", "curl", "wget", "ping", "ip", "ifconfig", "netstat",
            "ss", "ps", "kill", "top", "htop", "lsof", "mount", "fdisk",
            "blkid", "test", "command", "type", "alias", "export", "set",
            "mktemp", "tmux", "screen", "vi", "vim", "nano", "emacs",
            "git", "npm", "yarn", "pip", "python", "python3", "node",
            "ruby", "java", "javac", "gcc", "g++", "clang", "make",
            "cmake", "cargo", "rustc", "dotnet", "go", "swift",
            "docker", "kubectl", "terraform", "ansible", "ssh", "scp",
            "rsync", "chmod", "chown", "mkdir", "rm", "cp", "mv", "ln",
            "touch", "tee", "split", "join", "paste", "comm", "fold",
            "fmt", "pr", "xargs", "md5sum", "sha1sum", "sha256sum",
            "base64", "xxd", "od", "hexdump", "dd", "jq", "patch",
            "cmp", "diff3", "sdiff", "uname"
        };

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

    public static bool IsShellCommandAllowed(string firstWord, HashSet<string> builtInAllowed)
    {
        if (string.IsNullOrWhiteSpace(firstWord))
            return false;

        var extensionless = Path.GetFileNameWithoutExtension(firstWord);
        if (builtInAllowed.Contains(firstWord) || builtInAllowed.Contains(extensionless))
            return true;

        lock (SessionAllowedShellCommandsLock)
        {
            if (SessionAllowedShellCommands.Contains(firstWord) ||
                SessionAllowedShellCommands.Contains(extensionless))
            {
                return true;
            }
        }

        var config = AgentConfig.Load();
        return config.ShellAllowedCommands?.Any(command =>
            string.Equals(command, firstWord, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(command, extensionless, StringComparison.OrdinalIgnoreCase)) == true;
    }

    public static bool PromptToAllowShellCommand(string firstWord, string command)
    {
        if (Console.IsInputRedirected || Console.IsOutputRedirected)
            return false;

        var displayCommand = command.Length > 500 ? command[..500] + "..." : command;
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[yellow]Command '{firstWord.EscapeMarkup()}' is not in the built-in allowlist.[/]");
        AnsiConsole.MarkupLine("[dim]Full command:[/]");
        AnsiConsole.MarkupLine(displayCommand.EscapeMarkup());

        var choice = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("Allow this command?")
                .AddChoices("Allow once", "Allow for this session", "Allow always", "Deny"));

        if (choice == "Deny")
            return false;

        if (choice == "Allow for this session")
            AddSessionAllowedShellCommand(firstWord);

        if (choice == "Allow always")
        {
            AddSessionAllowedShellCommand(firstWord);
            AddPersistedAllowedShellCommand(firstWord);
        }

        return true;
    }

    private static void AddSessionAllowedShellCommand(string command)
    {
        lock (SessionAllowedShellCommandsLock)
        {
            SessionAllowedShellCommands.Add(command);
            var extensionless = Path.GetFileNameWithoutExtension(command);
            if (!string.IsNullOrWhiteSpace(extensionless))
                SessionAllowedShellCommands.Add(extensionless);
        }
    }

    private static void AddPersistedAllowedShellCommand(string command)
    {
        var config = AgentConfig.Load();
        var commands = new HashSet<string>(config.ShellAllowedCommands ?? [], StringComparer.OrdinalIgnoreCase)
        {
            command
        };

        var extensionless = Path.GetFileNameWithoutExtension(command);
        if (!string.IsNullOrWhiteSpace(extensionless))
            commands.Add(extensionless);

        config.ShellAllowedCommands = commands
            .OrderBy(item => item, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        config.Values["SHELL_ALLOWED_COMMANDS"] = string.Join(",", config.ShellAllowedCommands);
        config.Save();
    }
}
