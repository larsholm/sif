using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
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

        if (enabled.Contains("context") || enabled.Contains("ctx"))
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

            tools.Add(OpenAI.Chat.ChatTool.CreateFunctionTool(
                "ctx_stats",
                "Show local context store statistics for this pico session.",
                BinaryData.FromString("""
                    {
                        "type": "object",
                        "properties": {}
                    }
                    """)
            ));
        }

        if (enabled.Contains("diagnostics"))
        {
            tools.Add(CreateDiagnosticsTool("diagnostics"));
        }

        if (enabled.Contains("debug"))
        {
            tools.Add(CreateDiagnosticsTool("debug"));
        }

        return tools;
    }

    private static OpenAI.Chat.ChatTool CreateDiagnosticsTool(string name)
    {
        return OpenAI.Chat.ChatTool.CreateFunctionTool(
            name,
            "Get pico agent diagnostics only: configuration, AGENT_ environment variables, or current chat history. This is not a debugger and cannot launch, attach to, or manage .NET debug adapter sessions.",
            BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "item": { "type": "string", "enum": ["config", "env", "history"], "description": "Diagnostic item: 'config' for configuration content, 'env' for AGENT_ environment variables, 'history' for current conversation history." }
                    },
                    "required": ["item"]
                }
                """)
        );
    }

    public static Func<string, Task<string>>? DiagnosticsHandler { get; set; }

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
            "debug" => await RunDiagnosticsAsync(argumentsJson),
            "diagnostics" => await RunDiagnosticsAsync(argumentsJson),
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

    private static async Task<string> RunDiagnosticsAsync(string argsJson)
    {
        using var doc = JsonDocument.Parse(argsJson);
        var root = doc.RootElement;
        var item = root.GetProperty("item").GetString() ?? "";

        if (item == "history")
        {
            if (DiagnosticsHandler == null) return "Error: Diagnostics handler not registered.";
            return await DiagnosticsHandler(item);
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

        return $"Error: Unknown diagnostics item '{item}'";
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

        var logPath = Path.Combine(Path.GetTempPath(), $"pico_http_{port}_{Guid.NewGuid():N}.log");
        var scriptPath = Path.Combine(Path.GetTempPath(), $"pico_http_{Guid.NewGuid():N}.sh");
        var escapedPath = path.Replace("'", "'\"'\"'");
        var escapedLog = logPath.Replace("'", "'\"'\"'");
        var escapedBind = bind.Replace("'", "'\"'\"'");

        File.WriteAllText(scriptPath,
            "#!/bin/bash\n" +
            $"cd '{escapedPath}'\n" +
            $"exec python3 -m http.server {port} --bind '{escapedBind}' >'{escapedLog}' 2>&1\n");

        try
        {
            var chmod = Process.Start(new ProcessStartInfo("chmod", $"+x \"{scriptPath}\"") { UseShellExecute = false });
            chmod?.WaitForExit();

            var psi = new ProcessStartInfo
            {
                FileName = "setsid",
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add(scriptPath);

            var process = Process.Start(psi);
            if (process is null)
                return "Error: Failed to start HTTP server.";

            Thread.Sleep(300);
            if (process.HasExited)
            {
                var log = File.Exists(logPath) ? File.ReadAllText(logPath).Trim() : "";
                return string.IsNullOrWhiteSpace(log)
                    ? $"Error: HTTP server exited immediately with code {process.ExitCode}."
                    : $"Error: HTTP server exited immediately with code {process.ExitCode}.\n{log}";
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

    private static async Task<string> RunBashAsync(string argsJson, CancellationToken cancellationToken)
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
