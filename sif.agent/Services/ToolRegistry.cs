using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using OpenAI;
using Spectre.Console;

namespace sif.agent;

/// <summary>
/// Defines and executes tools available to the agent.
/// </summary>
internal static class ToolRegistry
{
    private static readonly HashSet<string> SessionAllowedShellCommands = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object SessionAllowedShellCommandsLock = new();

    public static List<OpenAI.Chat.ChatTool> GetTools(string[] enabled)
    {
        var tools = new List<OpenAI.Chat.ChatTool>();

        if (enabled.Contains("tool_catalog"))
        {
            tools.Add(OpenAI.Chat.ChatTool.CreateFunctionTool(
                "tool_catalog",
                "List or enable optional native tools.",
                BinaryData.FromString("""
                    {
                        "type": "object",
                        "properties": {
                            "enable": {
                                "type": "array",
                                "items": { "type": "string" },
                                "description": "Optional tool names to enable for the next step"
                            }
                        }
                    }
                    """)
            ));
        }

        if (enabled.Contains("bash"))
        {
            tools.Add(OpenAI.Chat.ChatTool.CreateFunctionTool(
                "bash",
                "Run an allowed shell command and return output. Uses Bash on Unix-like systems and PowerShell on Windows. Times out after 30s; use serve for static HTTP servers.",
                BinaryData.FromString("""
                    {
                        "type": "object",
                        "properties": {
                            "command": { "type": "string", "description": "The shell command to execute" },
                            "limit": { "type": "integer", "description": "Max output characters (default 8000)" }
                        },
                        "required": ["command"]
                    }
                    """)
            ));
        }

        if (enabled.Contains("read"))
        {
            tools.Add(OpenAI.Chat.ChatTool.CreateFunctionTool(
                "read",
                "Read the contents of a file. Returns text for text files, file info for binary files.",
                BinaryData.FromString("""
                    {
                        "type": "object",
                        "properties": {
                            "path": { "type": "string", "description": "Path to the file to read" },
                            "skiplines": { "type": "integer", "description": "Number of lines to skip from the start (default 0)" },
                            "limit": { "type": "integer", "description": "Max lines to read (default 200)" }
                        },
                        "required": ["path"]
                    }
                    """)
            ));
        }

        if (enabled.Contains("edit"))
        {
            tools.Add(OpenAI.Chat.ChatTool.CreateFunctionTool(
                "edit",
                "Edit a file by replacing old text with new text. The old text must match exactly.",
                BinaryData.FromString("""
                    {
                        "type": "object",
                        "properties": {
                            "path": { "type": "string", "description": "Path to the file to edit" },
                            "oldText": { "type": "string", "description": "The exact text to replace" },
                            "newText": { "type": "string", "description": "The replacement text" }
                        },
                        "required": ["path", "oldText", "newText"]
                    }
                    """)
            ));
        }

        if (enabled.Contains("write"))
        {
            tools.Add(OpenAI.Chat.ChatTool.CreateFunctionTool(
                "write",
                "Write content to a file. Creates the file if it doesn't exist, overwrites if it does.",
                BinaryData.FromString("""
                    {
                        "type": "object",
                        "properties": {
                            "path": { "type": "string", "description": "Path to the file to write" },
                            "content": { "type": "string", "description": "Content to write to the file" }
                        },
                        "required": ["path", "content"]
                    }
                    """)
            ));
        }

        if (enabled.Contains("sleep"))
        {
            tools.Add(OpenAI.Chat.ChatTool.CreateFunctionTool(
                "sleep",
                "Pause execution for a short period of time. Useful when waiting before retrying an operation.",
                BinaryData.FromString("""
                    {
                        "type": "object",
                        "properties": {
                            "seconds": { "type": "number", "description": "Number of seconds to wait, from 0 to 60" }
                        },
                        "required": ["seconds"]
                    }
                    """)
            ));
        }

        if (enabled.Contains("serve"))
        {
            tools.Add(OpenAI.Chat.ChatTool.CreateFunctionTool(
                "serve",
                "Start a local static HTTP server for a directory and return immediately with its URL and process id.",
                BinaryData.FromString("""
                    {
                        "type": "object",
                        "properties": {
                            "path": { "type": "string", "description": "Directory to serve. Defaults to the current working directory." },
                            "port": { "type": "integer", "description": "Port to use. Use 0 or omit to choose a free port." },
                            "bind": { "type": "string", "description": "Bind address. Defaults to 127.0.0.1." }
                        }
                    }
                    """)
            ));
        }

        if (enabled.Contains("context") || enabled.Contains("ctx") || enabled.Contains("ctx_index"))
        {
            tools.Add(OpenAI.Chat.ChatTool.CreateFunctionTool(
                "ctx_index",
                "Store large text in the local context store and return a compact handle for later search/read.",
                BinaryData.FromString("""
                    {
                        "type": "object",
                        "properties": {
                            "source": { "type": "string", "description": "Short label describing where this content came from" },
                            "content": { "type": "string", "description": "The text content to store out-of-band" }
                        },
                        "required": ["source", "content"]
                    }
                    """)
            ));
        }

        if (enabled.Contains("context") || enabled.Contains("ctx") || enabled.Contains("ctx_search"))
        {
            tools.Add(OpenAI.Chat.ChatTool.CreateFunctionTool(
                "ctx_search",
                "Search previously stored large context and return focused snippets instead of full blobs.",
                BinaryData.FromString("""
                    {
                        "type": "object",
                        "properties": {
                            "query": { "type": "string", "description": "Search terms" },
                            "limit": { "type": "integer", "description": "Maximum result count (default 8)" }
                        },
                        "required": ["query"]
                    }
                    """)
            ));
        }

        if (enabled.Contains("context") || enabled.Contains("ctx") || enabled.Contains("ctx_read"))
        {
            tools.Add(OpenAI.Chat.ChatTool.CreateFunctionTool(
                "ctx_read",
                "Read stored context by id. Use query to retrieve a focused snippet from a large stored blob.",
                BinaryData.FromString("""
                    {
                        "type": "object",
                        "properties": {
                            "id": { "type": "string", "description": "Context id returned by ctx_index or automatic context storage" },
                            "query": { "type": "string", "description": "Optional search focus within this context blob" },
                            "maxChars": { "type": "integer", "description": "Maximum characters to return (default 6000)" }
                        },
                        "required": ["id"]
                    }
                    """)
            ));
        }

        if (enabled.Contains("context") || enabled.Contains("ctx") || enabled.Contains("ctx_summarize"))
        {
            tools.Add(OpenAI.Chat.ChatTool.CreateFunctionTool(
                "ctx_summarize",
                "Generate a focused summary of stored context using the LLM. Use when you need to understand what's in a large stored blob but don't know the right search terms.",
                BinaryData.FromString("""
                    {
                        "type": "object",
                        "properties": {
                            "id": { "type": "string", "description": "Context id to summarize" },
                            "focus": { "type": "string", "description": "What to focus the summary on (e.g. 'error messages', 'configuration values', 'API endpoints'). Defaults to most important information if omitted." }
                        },
                        "required": ["id"]
                    }
                    """)
            ));
        }

        if (enabled.Contains("context") || enabled.Contains("ctx") || enabled.Contains("ctx_summarize"))
        {
            tools.Add(OpenAI.Chat.ChatTool.CreateFunctionTool(
                "ctx_summarize",
                "Generate a focused summary of stored context using the LLM. Use when you need to understand what's in a large stored blob but don't know the right search terms.",
                BinaryData.FromString("""
                    {
                        "type": "object",
                        "properties": {
                            "id": { "type": "string", "description": "Context id to summarize" },
                            "focus": { "type": "string", "description": "What to focus the summary on (e.g. 'error messages', 'configuration values', 'API endpoints'). Defaults to most important information if omitted." }
                        },
                        "required": ["id"]
                    }
                    """)
            ));
        }

        if (enabled.Contains("context") || enabled.Contains("ctx") || enabled.Contains("ctx_stats"))
        {
            tools.Add(OpenAI.Chat.ChatTool.CreateFunctionTool(
                "ctx_stats",
                "Show local context store statistics for this sif session.",
                BinaryData.FromString("""
                    {
                        "type": "object",
                        "properties": {}
                    }
                    """)
            ));
        }

        return tools;
    }

    public static async Task<string> ExecuteAsync(string toolName, string argumentsJson, CancellationToken cancellationToken = default)
    {
        return toolName switch
        {
            "bash" => await RunBashAsync(argumentsJson, cancellationToken),
            "read" => RunRead(argumentsJson),
            "edit" => RunEdit(argumentsJson),
            "write" => RunWrite(argumentsJson),
            "sleep" => await RunSleepAsync(argumentsJson, cancellationToken),
            "serve" => RunServe(argumentsJson),
            "ctx_index" => RunContextIndex(argumentsJson),
            "ctx_search" => RunContextSearch(argumentsJson),
            "ctx_read" => RunContextRead(argumentsJson),
            "ctx_stats" => ContextStore.Stats(),
            _ => $"Error: Unknown tool '{toolName}'"
        };
    }

    private static string RunContextIndex(string argsJson)
    {
        using var doc = JsonDocument.Parse(argsJson);
        var root = doc.RootElement;
        var source = root.TryGetProperty("source", out var s) ? s.GetString() ?? "manual" : "manual";
        var content = root.GetProperty("content").GetString() ?? "";
        return ContextStore.StoreAndDescribe(source, content);
    }

    private static string RunContextSearch(string argsJson)
    {
        using var doc = JsonDocument.Parse(argsJson);
        var root = doc.RootElement;
        var query = root.GetProperty("query").GetString() ?? "";
        var limit = root.TryGetProperty("limit", out var l) ? l.GetInt32() : 8;
        return ContextStore.Search(query, limit);
    }

    private static string RunContextRead(string argsJson)
    {
        using var doc = JsonDocument.Parse(argsJson);
        var root = doc.RootElement;
        var id = root.GetProperty("id").GetString() ?? "";
        var query = root.TryGetProperty("query", out var q) ? q.GetString() : null;
        var maxChars = root.TryGetProperty("maxChars", out var m) ? m.GetInt32() : 6000;
        return ContextStore.Read(id, query, maxChars);
    }

    private static async Task<string> RunSleepAsync(string argsJson, CancellationToken cancellationToken)
    {
        using var doc = JsonDocument.Parse(argsJson);
        var root = doc.RootElement;
        var seconds = root.TryGetProperty("seconds", out var s) ? s.GetDouble() : 0;

        if (double.IsNaN(seconds) || double.IsInfinity(seconds))
            return "Error: seconds must be a finite number.";
        if (seconds < 0)
            return "Error: seconds must be greater than or equal to 0.";
        if (seconds > 60)
            return "Error: seconds must be less than or equal to 60.";

        await Task.Delay(TimeSpan.FromSeconds(seconds), cancellationToken);
        return $"Slept for {seconds:0.###} seconds.";
    }

    private static string RunServe(string argsJson)
    {
        using var doc = JsonDocument.Parse(argsJson);
        var root = doc.RootElement;
        var pathArg = root.TryGetProperty("path", out var p) ? p.GetString() : null;
        var path = ResolvePath(pathArg ?? "");
        var bind = root.TryGetProperty("bind", out var b) ? b.GetString() ?? "127.0.0.1" : "127.0.0.1";
        var port = root.TryGetProperty("port", out var portElement) ? portElement.GetInt32() : 0;

        if (!Directory.Exists(path))
            return $"Error: Directory not found: {path}";
        if (port < 0 || port > 65535)
            return "Error: port must be between 0 and 65535.";
        if (bind is not "127.0.0.1" and not "localhost" and not "0.0.0.0")
            return "Error: bind must be 127.0.0.1, localhost, or 0.0.0.0.";

        if (port == 0)
            port = GetFreeTcpPort(IPAddress.Loopback);

        var logPath = Path.Combine(Path.GetTempPath(), $"sif_http_{port}_{Guid.NewGuid():N}.log");
        try
        {
            var python = FindExecutable(RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? ["python", "py"]
                : ["python3", "python"]);
            if (python == null)
                return "Error: Python was not found. Install Python or add it to PATH to use the serve tool.";

            File.WriteAllText(logPath, "");
            var psi = new ProcessStartInfo
            {
                FileName = python,
                WorkingDirectory = path,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            if (Path.GetFileNameWithoutExtension(python).Equals("py", StringComparison.OrdinalIgnoreCase))
                psi.ArgumentList.Add("-3");
            psi.ArgumentList.Add("-m");
            psi.ArgumentList.Add("http.server");
            psi.ArgumentList.Add(port.ToString());
            psi.ArgumentList.Add("--bind");
            psi.ArgumentList.Add(bind);

            var process = Process.Start(psi);
            if (process is null)
                return "Error: Failed to start HTTP server.";

            _ = PipeToLogAsync(process.StandardOutput, logPath);
            _ = PipeToLogAsync(process.StandardError, logPath);

            Thread.Sleep(300);
            if (process.HasExited)
            {
                var logText = File.Exists(logPath) ? File.ReadAllText(logPath).Trim() : "";
                return string.IsNullOrWhiteSpace(logText)
                    ? $"Error: HTTP server exited immediately with code {process.ExitCode}."
                    : $"Error: HTTP server exited immediately with code {process.ExitCode}.\n{logText}";
            }

            return $"Serving {path} at http://{bind}:{port}/\nPID: {process.Id}\nLog: {logPath}";
        }
        catch (Exception ex)
        {
            return $"Error starting HTTP server: {ex.Message}";
        }
    }

    private static int GetFreeTcpPort(IPAddress address)
    {
        using var listener = new TcpListener(address, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static async Task PipeToLogAsync(StreamReader reader, string logPath)
    {
        try
        {
            while (await reader.ReadLineAsync() is { } line)
                await File.AppendAllTextAsync(logPath, line + Environment.NewLine);
        }
        catch
        {
            // Best-effort logging for background server processes.
        }
    }

    private static string? FindExecutable(IEnumerable<string> names)
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

    private static async Task<string> RunBashAsync(string argsJson, CancellationToken cancellationToken)
    {
        using var doc = JsonDocument.Parse(argsJson);
        var root = doc.RootElement;
        var command = root.GetProperty("command").GetString() ?? "";
        var limit = root.TryGetProperty("limit", out var l) ? l.GetInt32() : 8000;

        var firstWord = GetFirstCommandWord(command);
        var allowed = GetAllowedShellCommands();
        if (!IsShellCommandAllowed(firstWord, allowed) && !PromptToAllowShellCommand(firstWord, command))
            return $"Error: Command '{firstWord}' not allowed.";

        try
        {
            var scriptPath = CreateShellScript(command, out var psi);

            try
            {
                using var process = Process.Start(psi);
                if (process is null) return "Error: Failed to start command.";

                var outputTask = process.StandardOutput.ReadToEndAsync();
                var errorTask = process.StandardError.ReadToEndAsync();
                var waitTask = process.WaitForExitAsync(cancellationToken);

                try
                {
                    if (await Task.WhenAny(waitTask, Task.Delay(TimeSpan.FromSeconds(30))) != waitTask)
                    {
                        try { process.Kill(true); } catch { }
                        return "Error: Command timed out after 30s.";
                    }

                    await Task.WhenAll(outputTask, errorTask, waitTask);
                }
                catch (OperationCanceledException)
                {
                    try { process.Kill(true); } catch { }
                    throw;
                }

                cancellationToken.ThrowIfCancellationRequested();
                var output = outputTask.Result;
                var errors = errorTask.Result;

                var result = output.Trim();
                if (!string.IsNullOrEmpty(errors))
                    result += (result.Length > 0 ? "\n" : "") + errors.Trim();

                if (result.Length > limit)
                    result = result.Substring(0, limit) + $"\n... (truncated, {result.Length - limit} more chars)";

                return string.IsNullOrEmpty(result) ? "(no output)" : result;
            }
            finally
            {
                try { File.Delete(scriptPath); } catch { }
            }
        }
        catch (Exception ex)
        {
            return $"Error executing command: {ex.Message}";
        }
    }

    private static string GetFirstCommandWord(string command)
    {
        var trimmed = command.TrimStart();
        if (trimmed.Length == 0)
            return "";

        if (trimmed[0] is '"' or '\'')
        {
            var quote = trimmed[0];
            var end = trimmed.IndexOf(quote, 1);
            return end > 1 ? trimmed[1..end] : trimmed.Trim(quote);
        }

        return trimmed.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault() ?? "";
    }

    private static HashSet<string> GetAllowedShellCommands()
    {
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "ls", "cat", "grep", "find", "pwd", "cd", "csi", "head", "tail", "wc", "whoami",
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

    private static bool IsShellCommandAllowed(string firstWord, HashSet<string> builtInAllowed)
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

    private static bool PromptToAllowShellCommand(string firstWord, string command)
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

    private static string CreateShellScript(string command, out ProcessStartInfo psi)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var scriptPath = Path.Combine(Path.GetTempPath(), $"sif_shell_{Guid.NewGuid():N}.ps1");
            File.WriteAllText(scriptPath, command);
            var shell = FindExecutable(["pwsh", "powershell"]) ?? "powershell.exe";
            psi = new ProcessStartInfo
            {
                FileName = shell,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                WorkingDirectory = Environment.CurrentDirectory,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-NoProfile");
            if (Path.GetFileNameWithoutExtension(shell).Equals("powershell", StringComparison.OrdinalIgnoreCase))
            {
                psi.ArgumentList.Add("-ExecutionPolicy");
                psi.ArgumentList.Add("Bypass");
            }
            psi.ArgumentList.Add("-File");
            psi.ArgumentList.Add(scriptPath);
            return scriptPath;
        }

        var bashScriptPath = Path.Combine(Path.GetTempPath(), $"sif_bash_{Guid.NewGuid():N}.sh");
        File.WriteAllText(bashScriptPath, "#!/bin/bash\n" + command);
        var chmod = Process.Start(new ProcessStartInfo("chmod", $"+x \"{bashScriptPath}\"") { UseShellExecute = false });
        if (chmod != null) chmod.WaitForExit();

        psi = new ProcessStartInfo
        {
            FileName = bashScriptPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = Environment.CurrentDirectory
        };
        return bashScriptPath;
    }

    private static string RunRead(string argsJson)
    {
        using var doc = JsonDocument.Parse(argsJson);
        var root = doc.RootElement;
        var path = ResolvePath(root.GetProperty("path").GetString() ?? "");
        var skiplines = root.TryGetProperty("skiplines", out var s) ? s.GetInt32() : 0;
        var limit = root.TryGetProperty("limit", out var l) ? l.GetInt32() : 200;

        if (!File.Exists(path))
            return $"Error: File not found: {path}";

        var ext = Path.GetExtension(path).ToLowerInvariant();
        var textExts = new HashSet<string> { ".cs", ".json", ".xml", ".txt", ".md", ".yaml", ".yml", ".sh", ".bash", ".py", ".js", ".ts", ".go", ".rs", ".java", ".c", ".cpp", ".h", ".hpp", ".html", ".css", ".sql", ".toml", ".ini", ".cfg", ".conf", ".env", ".nuspec", ".csproj", ".sln", ".editorconfig", ".gitignore", ".rb", ".php", ".pl", ".lua", ".swift", ".kt", ".scala", ".r", ".m", ".mm", ".dart", ".v", ".sv", ".vhd", ".vhdl", ".asm", ".s", ".S", ".tex", ".bib", ".csv", ".log", ".diff", ".patch", ".properties", ".gradle" };

        if (textExts.Contains(ext) || path.Contains(".gitignore") || path.Contains(".editorconfig") || path.Contains("Makefile") || path.Contains("Dockerfile") || path.Contains("Vagrantfile"))
        {
            var lines = File.ReadAllLines(path);
            if (lines.Length > skiplines) lines = lines.Skip(skiplines).ToArray();
            if (lines.Length > limit) return string.Join('\n', lines.Take(limit)) + $"\n... ({lines.Length - limit} more lines)";
            return string.Join('\n', lines);
        }

        var size = new FileInfo(path).Length;
        return $"Binary file ({ext}, {size:N0} bytes)";
    }

    private static string RunEdit(string argsJson)
    {
        using var doc = JsonDocument.Parse(argsJson);
        var root = doc.RootElement;
        var path = ResolvePath(root.GetProperty("path").GetString() ?? "");
        var oldText = root.GetProperty("oldText").GetString() ?? "";
        var newText = root.GetProperty("newText").GetString() ?? "";

        if (!File.Exists(path)) return $"Error: File not found: {path}";

        var content = File.ReadAllText(path);
        if (!content.Contains(oldText))
        {
            if (content.Contains(oldText, StringComparison.OrdinalIgnoreCase)) return "Error: Text not found (case mismatch). The text must match exactly including whitespace.";
            return $"Error: Text not found in {path}";
        }

        var newContent = content.Replace(oldText, newText, StringComparison.Ordinal);
        File.WriteAllText(path, newContent);
        return $"Edited {path} successfully.";
    }

    private static string RunWrite(string argsJson)
    {
        using var doc = JsonDocument.Parse(argsJson);
        var root = doc.RootElement;
        var path = ResolvePath(root.GetProperty("path").GetString() ?? "");
        var content = root.GetProperty("content").GetString() ?? "";

        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        File.WriteAllText(path, content);
        return $"Wrote {path} ({content.Length} chars).";
    }

    private static string ResolvePath(string path)
    {
        if (string.IsNullOrEmpty(path)) return Environment.CurrentDirectory;
        return Path.IsPathRooted(path) ? path : Path.Combine(Environment.CurrentDirectory, path);
    }
}
