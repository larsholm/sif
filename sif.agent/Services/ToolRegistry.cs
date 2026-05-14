using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using Spectre.Console;
using sif.agent.Services.Tools;

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
                                "description": "Tool names to enable"
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
                "Run shell commands. Bash on Unix, PowerShell on Windows. Default 30s timeout; use serve for long-running servers.",
                BinaryData.FromString("""
                    {
                        "type": "object",
                        "properties": {
                            "command": { "type": "string", "description": "Shell command to run" },
                            "limit": { "type": "integer", "description": "Max output chars (default 24000, max 120000)" },
                            "timeout": { "type": "number", "description": "Timeout in seconds (default 30, max 300)" }
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
                "Read file contents. Text for text files, file info for binary files.",
                BinaryData.FromString("""
                    {
                        "type": "object",
                        "properties": {
                            "path": { "type": "string", "description": "File path" },
                            "skiplines": { "type": "integer", "description": "Lines to skip from start (default 0)" },
                            "limit": { "type": "integer", "description": "Max lines (default 1000, max 5000)" }
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
                "Replace text in a file. Old text must match exactly.",
                BinaryData.FromString("""
                    {
                        "type": "object",
                        "properties": {
                            "path": { "type": "string", "description": "File path" },
                            "oldText": { "type": "string", "description": "Exact text to replace" },
                            "newText": { "type": "string", "description": "Replacement text" }
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
                "Write content to a file (creates or overwrites).",
                BinaryData.FromString("""
                    {
                        "type": "object",
                        "properties": {
                            "path": { "type": "string", "description": "File path" },
                            "content": { "type": "string", "description": "File content" }
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
                "Wait a number of seconds.",
                BinaryData.FromString("""
                    {
                        "type": "object",
                        "properties": {
                            "seconds": { "type": "number", "description": "Seconds to wait (0-60)" }
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
                "Start a local static HTTP server for a directory and return its URL.",
                BinaryData.FromString("""
                    {
                        "type": "object",
                        "properties": {
                            "path": { "type": "string", "description": "Directory to serve (default: CWD)" },
                            "port": { "type": "integer", "description": "Port (0 or omit for auto)" },
                            "bind": { "type": "string", "description": "Bind address (default 127.0.0.1)" }
                        }
                    }
                    """)
            ));
        }

        if (enabled.Contains("context") || enabled.Contains("ctx") || enabled.Contains("ctx_index"))
        {
            tools.Add(OpenAI.Chat.ChatTool.CreateFunctionTool(
                "ctx_index",
                "Store large text in the local context store and return a handle.",
                BinaryData.FromString("""
                    {
                        "type": "object",
                        "properties": {
                            "source": { "type": "string", "description": "Short label describing source" },
                            "content": { "type": "string", "description": "Text to store" }
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
                "Search stored context and return focused snippets.",
                BinaryData.FromString("""
                    {
                        "type": "object",
                        "properties": {
                            "query": { "type": "string", "description": "Search terms" },
                            "limit": { "type": "integer", "description": "Max results (default 8)" }
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
                "Read stored context by id. Use query to retrieve a focused snippet.",
                BinaryData.FromString("""
                    {
                        "type": "object",
                        "properties": {
                            "id": { "type": "string", "description": "Context id from ctx_index or auto-storage" },
                            "query": { "type": "string", "description": "Optional search focus within blob" },
                            "maxChars": { "type": "integer", "description": "Max characters to return (default 32000, max 160000)" }
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
                "Generate a focused summary of stored context. Use when unsure of search terms.",
                BinaryData.FromString("""
                    {
                        "type": "object",
                        "properties": {
                            "id": { "type": "string", "description": "Context id to summarize" },
                            "focus": { "type": "string", "description": "What to focus on (e.g. 'error messages', 'config values', 'API endpoints'). Defaults to most important if omitted." }
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
                "Show local context store statistics.",
                BinaryData.FromString("""
                    {
                        "type": "object",
                        "properties": {}
                    }
                    """)
            ));
        }

        if (enabled.Contains("roslyn"))
        {
            tools.Add(OpenAI.Chat.ChatTool.CreateFunctionTool(
                "roslyn_find_symbols",
                "Find symbols in a C# solution or project.",
                BinaryData.FromString("""
                    {
                        "type": "object",
                        "properties": {
                            "solutionPath": { "type": "string", "description": "Path to .sln file (alternative to projectPath)" },
                            "projectPath": { "type": "string", "description": "Path to .csproj file (alternative to solutionPath)" },
                            "name": { "type": "string", "description": "Symbol name to search for" }
                        },
                        "required": ["name"]
                    }
                    """)
            ));

            tools.Add(OpenAI.Chat.ChatTool.CreateFunctionTool(
                "roslyn_get_diagnostics",
                "Get diagnostic issues in a C# project.",
                BinaryData.FromString("""
                    {
                        "type": "object",
                        "properties": {
                            "projectPath": { "type": "string", "description": "Path to .csproj or .sln" }
                        },
                        "required": ["projectPath"]
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
            "roslyn_find_symbols" => await RunRoslynFindSymbols(argumentsJson),
            "roslyn_get_diagnostics" => await RunRoslynGetDiagnostics(argumentsJson),
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
        var maxChars =
            root.TryGetProperty("maxChars", out var m1) ? m1.GetInt32() :
            root.TryGetProperty("max_chars", out var m2) ? m2.GetInt32() :
            32000;
        return ContextStore.Read(id, query, maxChars);
    }

    private static async Task<string> RunRoslynFindSymbols(string argsJson)
    {
        using var doc = JsonDocument.Parse(argsJson);
        var root = doc.RootElement;

        // Accept solutionPath, projectPath, or path (camelCase or snake_case)
        var path =
            root.TryGetProperty("solutionPath", out var sp1) ? sp1.GetString() ?? "" :
            root.TryGetProperty("solution_path", out var sp2) ? sp2.GetString() ?? "" :
            root.TryGetProperty("projectPath", out var pp1) ? pp1.GetString() ?? "" :
            root.TryGetProperty("project_path", out var pp2) ? pp2.GetString() ?? "" :
            root.TryGetProperty("project", out var pp) ? pp.GetString() ?? "" :
            root.TryGetProperty("path", out var p) ? p.GetString() ?? "" :
            "";
            
        var name = root.TryGetProperty("name", out var n) ? n.GetString() ?? "" : root.TryGetProperty("query", out var q) ? q.GetString() ?? "" : "";

        return await RoslynTools.FindSymbolsAsync(path, name);
    }

    private static async Task<string> RunRoslynGetDiagnostics(string argsJson)
    {
        using var doc = JsonDocument.Parse(argsJson);
        var root = doc.RootElement;

        // Accept multiple parameter name formats: camelCase, snake_case, or projectFilePath
        var projectPath =
            (root.TryGetProperty("path", out var p) ? p.GetString() :
             root.TryGetProperty("project", out var pp) ? pp.GetString() :
             root.TryGetProperty("projectPath", out var p1) ? p1.GetString() :
             root.TryGetProperty("project_path", out var p2) ? p2.GetString() :
             root.TryGetProperty("projectFilePath", out var p3) ? p3.GetString() :
             "") ?? "";

        return await RoslynTools.GetDiagnosticsAsync(projectPath);
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
        try
        {
            if (string.IsNullOrWhiteSpace(argsJson))
                return "Error: serve tool requires a JSON arguments object.";

            using var doc = JsonDocument.Parse(argsJson);
            var root = doc.RootElement;
            var pathArg = root.TryGetProperty("path", out var p) ? p.GetString() : null;
            var path = ResolvePath(pathArg ?? "");
            var bind = root.TryGetProperty("bind", out var b) ? b.GetString() ?? "127.0.0.1" : "127.0.0.1";

            int port;
            if (root.TryGetProperty("port", out var portElement))
            {
                if (portElement.ValueKind != JsonValueKind.Number)
                    return "Error: port must be a number.";
                port = portElement.GetInt32();
            }
            else
            {
                port = 0;
            }

            if (!Directory.Exists(path))
                return $"Error: Directory not found: {path}";
            if (port < 0 || port > 65535)
                return "Error: port must be between 0 and 65535.";
            if (bind is not "127.0.0.1" and not "localhost" and not "0.0.0.0")
                return "Error: bind must be 127.0.0.1, localhost, or 0.0.0.0.";

            if (port == 0)
                port = GetFreeTcpPort(IPAddress.Loopback);

            var logPath = Path.Combine(Path.GetTempPath(), $"sif_http_{port}_{Guid.NewGuid():N}.log");

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
        catch (JsonException ex)
        {
            return $"Error: Invalid JSON arguments: {ex.Message}";
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
        var limit = root.TryGetProperty("limit", out var l) ? l.GetInt32() : 24000;
        limit = Math.Clamp(limit, 1000, 120000);

        // Parse timeout parameter (default 30s, max 300s, min 1s)
        var timeoutSeconds = 30.0;
        if (root.TryGetProperty("timeout", out var t))
        {
            if (t.ValueKind == JsonValueKind.Number && t.TryGetDouble(out var parsedTimeout))
            {
                if (double.IsNaN(parsedTimeout) || double.IsInfinity(parsedTimeout))
                    return "Error: timeout must be a finite number.";
                if (parsedTimeout < 1)
                    return "Error: timeout must be at least 1 second.";
                if (parsedTimeout > 300)
                    return "Error: timeout must be at most 300 seconds (5 minutes).";
                timeoutSeconds = parsedTimeout;
            }
            else
            {
                return "Error: timeout must be a number.";
            }
        }
        var timeout = TimeSpan.FromSeconds(timeoutSeconds);

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
                    if (await Task.WhenAny(waitTask, Task.Delay(timeout, cancellationToken)) != waitTask)
                    {
                        await TerminateProcessTreeAsync(process);
                        await Task.WhenAny(Task.WhenAll(outputTask, errorTask), Task.Delay(TimeSpan.FromSeconds(1)));
                        var output = ReadCompletedOrEmpty(outputTask);
                        var errors = ReadCompletedOrEmpty(errorTask);
                        var partial = FormatCommandResult(output, errors, limit);
                        return string.IsNullOrEmpty(partial)
                            ? $"Error: Command timed out after {timeoutSeconds:0.##}s."
                            : $"Error: Command timed out after {timeoutSeconds:0.##}s. Partial output:\n{partial}";
                    }

                    await waitTask;
                    var streamsTask = Task.WhenAll(outputTask, errorTask);
                    if (await Task.WhenAny(streamsTask, Task.Delay(TimeSpan.FromSeconds(1))) != streamsTask)
                    {
                        var output = ReadCompletedOrEmpty(outputTask);
                        var errors = ReadCompletedOrEmpty(errorTask);
                        var partial = FormatCommandResult(output, errors, limit);
                        return string.IsNullOrEmpty(partial)
                            ? "Command exited, but output streams stayed open. A background process may still be running."
                            : partial + "\n... (command exited, but output streams stayed open; a background process may still be running)";
                    }
                }
                catch (OperationCanceledException)
                {
                    await TerminateProcessTreeAsync(process);
                    throw;
                }

                cancellationToken.ThrowIfCancellationRequested();
                var result = FormatCommandResult(outputTask.Result, errorTask.Result, limit);
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

    private static async Task TerminateProcessTreeAsync(Process process)
    {
        try
        {
            if (process.HasExited)
                return;
        }
        catch
        {
            return;
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            try { process.Kill(true); } catch { }
            return;
        }

        var pids = GetUnixDescendantPids(process.Id)
            .Append(process.Id)
            .Distinct()
            .Reverse()
            .ToList();

        SignalUnixProcesses(pids, "TERM");
        await Task.Delay(750);

        try
        {
            if (process.HasExited)
                return;
        }
        catch
        {
            return;
        }

        SignalUnixProcesses(pids, "KILL");

        try
        {
            if (!process.HasExited)
                process.Kill();
        }
        catch
        {
            // Best-effort cleanup; callers still return timeout/cancel promptly.
        }
    }

    private static List<int> GetUnixDescendantPids(int rootPid)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "ps",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("-axo");
            psi.ArgumentList.Add("pid=,ppid=");
            using var ps = Process.Start(psi);

            if (ps == null)
                return [];

            var output = ps.StandardOutput.ReadToEnd();
            ps.WaitForExit(1000);

            var childrenByParent = new Dictionary<int, List<int>>();
            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (parts.Length < 2 || !int.TryParse(parts[0], out var pid) || !int.TryParse(parts[1], out var parentPid))
                    continue;

                if (!childrenByParent.TryGetValue(parentPid, out var children))
                {
                    children = [];
                    childrenByParent[parentPid] = children;
                }
                children.Add(pid);
            }

            var descendants = new List<int>();
            var stack = new Stack<int>();
            stack.Push(rootPid);
            while (stack.Count > 0)
            {
                var parent = stack.Pop();
                if (!childrenByParent.TryGetValue(parent, out var children))
                    continue;

                foreach (var child in children)
                {
                    descendants.Add(child);
                    stack.Push(child);
                }
            }

            return descendants;
        }
        catch
        {
            return [];
        }
    }

    private static void SignalUnixProcesses(IEnumerable<int> pids, string signal)
    {
        foreach (var pid in pids)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "kill",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                psi.ArgumentList.Add($"-{signal}");
                psi.ArgumentList.Add(pid.ToString());
                using var kill = Process.Start(psi);
                kill?.WaitForExit(1000);
            }
            catch
            {
                // Best-effort process cleanup.
            }
        }
    }

    private static string ReadCompletedOrEmpty(Task<string> task)
    {
        if (!task.IsCompleted)
            return "";

        try
        {
            return task.Result;
        }
        catch
        {
            return "";
        }
    }

    private static string FormatCommandResult(string output, string errors, int limit)
    {
        var result = output.Trim();
        if (!string.IsNullOrEmpty(errors))
            result += (result.Length > 0 ? "\n" : "") + errors.Trim();

        if (result.Length > limit)
            result = result.Substring(0, limit) + $"\n... (truncated, {result.Length - limit} more chars)";

        return result;
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
        var limit = root.TryGetProperty("limit", out var l) ? l.GetInt32() : 1000;
        limit = Math.Clamp(limit, 1, 5000);

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

        // Accept multiple parameter name formats: camelCase or snake_case
        var oldText =
            root.TryGetProperty("oldText", out var o1) ? o1.GetString() :
            root.TryGetProperty("old_text", out var o2) ? o2.GetString() :
            "";
        var newText =
            root.TryGetProperty("newText", out var n1) ? n1.GetString() :
            root.TryGetProperty("new_text", out var n2) ? n2.GetString() :
            "";

        // Validate inputs
        if (string.IsNullOrEmpty(path)) return "Error: path is required.";
        if (string.IsNullOrEmpty(oldText)) return "Error: oldText is required and must not be empty. The text to replace must be specified.";
        
        if (!File.Exists(path)) return $"Error: File not found: {path}";

        // Warn if file is not in a git repository
        var gitWarning = "";
        if (!IsPathInGitRepo(path))
        {
            gitWarning = GetGitWarning(path, "edit");
        }

        var content = File.ReadAllText(path);
        
        // Check if oldText exists in the file
        if (!content.Contains(oldText, StringComparison.Ordinal))
        {
            // Provide helpful diagnostics
            var fileName = Path.GetFileName(path);
            var oldTextLength = oldText.Length;
            var oldTextPreview = oldText.Length > 100 ? oldText[..100] + "..." : oldText;
            
            // Check for case mismatch
            if (content.Contains(oldText, StringComparison.OrdinalIgnoreCase))
                return $"Error: Text not found in {fileName} (case mismatch). The text must match exactly including case, whitespace, and line endings.\nSearched for ({oldTextLength} chars): {oldTextPreview}";
            
            // Check for whitespace differences (common issue)
            var normalizedOld = NormalizeWhitespace(oldText);
            var normalizedContent = NormalizeWhitespace(content);
            if (normalizedContent.Contains(normalizedOld, StringComparison.Ordinal))
                return $"Error: Text not found in {fileName} (whitespace/line ending mismatch). The text must match exactly including spaces, tabs, and line endings.\nSearched for ({oldTextLength} chars): {oldTextPreview}\nTip: Try copying the exact text from the file including whitespace.";
            
            return $"Error: Text not found in {fileName}. The exact text to replace was not found in the file.\nSearched for ({oldTextLength} chars): {oldTextPreview}\nTip: Use the read tool first to see the exact content and whitespace.";
        }

        // Check if replacement would actually change anything
        if (content.Contains(oldText, StringComparison.Ordinal))
        {
            var newContent = content.Replace(oldText, newText, StringComparison.Ordinal);
            if (newContent == content)
                return $"Warning: oldText and newText are identical. No changes made to {Path.GetFileName(path)}.";
            
            File.WriteAllText(path, newContent);
            var occurrences = CountOccurrences(content, oldText);
            var successMsg = $"Edited {Path.GetFileName(path)} successfully. Replaced {occurrences} occurrence(s) of the specified text.";
            return gitWarning + successMsg;
        }

        // Should not reach here due to check above, but defensively:
        return $"Error: Unexpected error editing {Path.GetFileName(path)}.";
    }

    private static string NormalizeWhitespace(string text)
    {
        return text.Replace("\r\n", "\n").Replace("\r", "\n").Replace(" ", "").Replace("\t", "");
    }

    private static int CountOccurrences(string text, string pattern)
    {
        if (string.IsNullOrEmpty(pattern)) return 0;
        int count = 0;
        int index = 0;
        while ((index = text.IndexOf(pattern, index, StringComparison.Ordinal)) != -1)
        {
            count++;
            index += pattern.Length;
        }
        return count;
    }

    private static string RunWrite(string argsJson)
    {
        using var doc = JsonDocument.Parse(argsJson);
        var root = doc.RootElement;
        var path = ResolvePath(root.GetProperty("path").GetString() ?? "");
        var content = root.GetProperty("content").GetString() ?? "";

        // Warn if file is not in a git repository
        var gitWarning = "";
        if (!IsPathInGitRepo(path))
        {
            gitWarning = GetGitWarning(path, "write");
        }

        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        File.WriteAllText(path, content);
        var successMsg = $"Wrote {path} ({content.Length} chars).";
        return gitWarning + successMsg;
    }

    private static string ResolvePath(string path)
    {
        if (string.IsNullOrEmpty(path)) return Environment.CurrentDirectory;
        return Path.IsPathRooted(path) ? path : Path.Combine(Environment.CurrentDirectory, path);
    }

    private static bool IsPathInGitRepo(string filePath)
    {
        try
        {
            var dir = Path.GetDirectoryName(filePath) ?? Environment.CurrentDirectory;
            
            // Walk up the directory tree looking for .git folder
            var currentDir = new DirectoryInfo(dir);
            while (currentDir != null)
            {
                if (Directory.Exists(Path.Combine(currentDir.FullName, ".git")))
                    return true;
                currentDir = currentDir.Parent;
            }
            
            return false;
        }
        catch
        {
            // If any error occurs, assume not in a git repo
            return false;
        }
    }

    private static string GetGitWarning(string filePath, string operation)
    {
        var fileName = Path.GetFileName(filePath);
        var dir = Path.GetDirectoryName(Path.GetFullPath(filePath)) ?? Environment.CurrentDirectory;
        
        return $"""
            ⚠️  Warning: {fileName} is not in a git repository.
            
            It's recommended to track files with git before {operation.ToLower()}ing them.
            To create a git repo in this directory:
              cd {dir}
              git init
              git add {fileName}
              git commit -m "Initial commit"
            
            Continuing with {operation.ToLower()}...
            """;
    }
}
