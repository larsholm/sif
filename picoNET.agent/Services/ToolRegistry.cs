using System.Diagnostics;
using System.Text;
using System.Text.Json;
using OpenAI;
using Spectre.Console;

namespace picoNET.agent;

/// <summary>
/// Defines and executes tools available to the agent.
/// </summary>
internal static class ToolRegistry
{
    public static List<OpenAI.Chat.ChatTool> GetTools(string[] enabled)
    {
        var tools = new List<OpenAI.Chat.ChatTool>();

        if (enabled.Contains("bash"))
        {
            tools.Add(OpenAI.Chat.ChatTool.CreateFunctionTool(
                "bash",
                "Execute a shell command and return its output. Only safe commands allowed: ls, cat, grep, find, pwd, head, tail, wc, whoami, hostname, date, env, echo, file, stat, du, df, git, curl, python, node, jq, sed, awk, sort, uniq, tr, cut, mkdir, cp, mv, rm, ln, touch, chmod, chown, tar, gzip, gunzip, zip, unzip, tree, less, more, strings, ping, ip, ifconfig, netstat, ss, ps, kill, top, lsof, mount, fdisk, blkid, test, command, type, alias, export, mktemp, vi, vim, nano, emacs, make, cmake, gcc, g++, clang, javac, java, ruby, swift, cl, rustc, cargo, dotnet, go, npm, yarn, pip, docker, kubectl, terraform, ansible, ssh, scp, rsync, diff, realpath, dirname, basename, xargs, tee, split, join, paste, comm, uniq, fmt, fold, pr, nroff, groff, man, info, apropos, whatis, locate, which, whereis, strings, file, stat, wc, head, tail, cut, paste, tr, sed, awk, grep, sort, uniq, comm, join, split, diff, cmp, patch, md5sum, sha1sum, sha256sum, base64, uuencode, uudecode, xxd, od, hexdump, dd, dd if=...",
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

        if (enabled.Contains("debug"))
        {
            tools.Add(OpenAI.Chat.ChatTool.CreateFunctionTool(
                "debug",
                "Get diagnostic information about the agent's environment, configuration, and state.",
                BinaryData.FromString("""
                    {
                        "type": "object",
                        "properties": {
                            "item": { "type": "string", "enum": ["config", "env", "history"], "description": "The item to debug: 'config' for configuration content, 'env' for environment variables, 'history' to see conversation history." }
                        },
                        "required": ["item"]
                    }
                    """)
            ));
        }

        return tools;
    }

    public static Func<string, Task<string>>? DebugHandler { get; set; }

    public static async Task<string> ExecuteAsync(string toolName, string argumentsJson)
    {
        return toolName switch
        {
            "bash" => await RunBashAsync(argumentsJson),
            "read" => RunRead(argumentsJson),
            "edit" => RunEdit(argumentsJson),
            "write" => RunWrite(argumentsJson),
            "debug" => await RunDebugAsync(argumentsJson),
            _ => $"Error: Unknown tool '{toolName}'"
        };
    }

    private static async Task<string> RunDebugAsync(string argsJson)
    {
        using var doc = JsonDocument.Parse(argsJson);
        var root = doc.RootElement;
        var item = root.GetProperty("item").GetString() ?? "";

        if (item == "history")
        {
            if (DebugHandler == null) return "Error: Debug handler not registered.";
            return await DebugHandler(item);
        }

        if (item == "config")
        {
            var path = AgentConfig.ConfigPath;
            if (File.Exists(path))
            {
                return $"Content of {path}:\n\n" + File.ReadAllText(path);
            }
            return $"Config file not found at: {path}";
        }
        else if (item == "env")
        {
            var sb = new StringBuilder();
            sb.AppendLine("AGENT_ environment variables:");
            foreach (System.Collections.DictionaryEntry de in Environment.GetEnvironmentVariables())
            {
                var key = de.Key.ToString() ?? "";
                if (key.StartsWith("AGENT_"))
                {
                    var val = de.Value?.ToString() ?? "";
                    if (key.Contains("KEY")) val = "****";
                    sb.AppendLine($"{key}={val}");
                }
            }
            return sb.ToString();
        }

        return $"Error: Unknown debug item '{item}'";
    }

    private static async Task<string> RunBashAsync(string argsJson)
    {
        using var doc = JsonDocument.Parse(argsJson);
        var root = doc.RootElement;
        var command = root.GetProperty("command").GetString() ?? "";
        var limit = root.TryGetProperty("limit", out var l) ? l.GetInt32() : 8000;

        var allowed = new HashSet<string>
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
            "cmp", "diff3", "sdiff"
        };

        var firstWord = command.Split(' ').FirstOrDefault()?.Trim() ?? "";
        if (!allowed.Contains(firstWord))
            return $"Error: Command '{firstWord}' not allowed.";

        try
        {
            var scriptPath = Path.Combine(Path.GetTempPath(), $"pico_bash_{Guid.NewGuid():N}.sh");
            File.WriteAllText(scriptPath, "#!/bin/bash\n" + command);
            var chmod = Process.Start(new ProcessStartInfo("chmod", $"+x \"{scriptPath}\"") { UseShellExecute = false });
            if (chmod != null) chmod.WaitForExit();

            var psi = new ProcessStartInfo
            {
                FileName = scriptPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                WorkingDirectory = Environment.CurrentDirectory
            };

            try
            {
                using var process = Process.Start(psi);
                if (process is null) return "Error: Failed to start command.";

                var outputTask = process.StandardOutput.ReadToEndAsync();
                var errorTask = process.StandardError.ReadToEndAsync();
                var waitTask = process.WaitForExitAsync();

                if (await Task.WhenAny(waitTask, Task.Delay(TimeSpan.FromSeconds(30))) != waitTask)
                {
                    try { process.Kill(true); } catch { }
                    return "Error: Command timed out after 30s.";
                }

                await Task.WhenAll(outputTask, errorTask);
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
