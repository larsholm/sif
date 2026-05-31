namespace sif.agent;

/// <summary>
/// Defines the OpenAI tool/function schemas exposed to the agent.
/// </summary>
internal static class ToolSchemas
{
    private static readonly object CacheLock = new();
    private static readonly Dictionary<string, OpenAI.Chat.ChatTool[]> CachedTools = new(StringComparer.Ordinal);

    public static List<OpenAI.Chat.ChatTool> GetTools(string[] enabled)
    {
        var cacheKey = GetCacheKey(enabled);
        lock (CacheLock)
        {
            if (CachedTools.TryGetValue(cacheKey, out var cached))
                return [.. cached];
        }

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
                "Run shell commands. Default 30s timeout, max 10 min. A non-zero exit code is reported automatically as '(exit code N)' \u2014 do not append '; echo $?' to capture it.",
                BinaryData.FromString("""
                    {
                        "type": "object",
                        "properties": {
                            "command": { "type": "string", "description": "Shell command to run" },
                            "limit": { "type": "integer", "description": "Max output chars (default 24000, max 120000)" },
                            "timeout": { "type": "number", "description": "Timeout in seconds" }
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
                            "skiplines": { "type": "integer", "description": "Lines to skip from start" },
                            "limit": { "type": "integer", "description": "Max lines to read (default 1000)" }
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
                            "oldText": { "type": "string", "description": "The exact literal text to replace (including all whitespace, indentation, newlines, and surrounding code etc.). Must match the file content exactly." },
                            "newText": { "type": "string", "description": "The exact literal text to replace 'oldText' with." }
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
                            "content": { "type": "string", "description": "Full file content to write" }
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
                            "seconds": { "type": "number", "description": "Seconds to wait, 0-60" }
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
                            "path": { "type": "string", "description": "Directory to serve (default CWD)" },
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
                            "source": { "type": "string", "description": "Short label (e.g. filename or URL)" },
                            "content": { "type": "string", "description": "Full text to index" }
                        },
                        "required": ["content"]
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
                            "query": { "type": "string", "description": "Search terms (aliases: q, search, text)" },
                            "limit": { "type": "integer", "description": "Max results (default 8; alias: max)" }
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
                            "id": { "type": "string", "description": "Context id" },
                            "query": { "type": "string", "description": "Optional search focus within blob" },
                            "maxChars": { "type": "integer", "description": "Max chars" }
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
                            "id": { "type": "string", "description": "Context id" },
                            "focus": { "type": "string", "description": "Summary focus" }
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
                            "solutionPath": { "type": "string", "description": ".sln path" },
                            "projectPath": { "type": "string", "description": ".csproj path" },
                            "name": { "type": "string", "description": "Symbol name" }
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
                            "projectPath": { "type": "string", "description": ".csproj or .sln path" }
                        },
                        "required": ["projectPath"]
                    }
                    """)
            ));
        }

        lock (CacheLock)
        {
            if (CachedTools.TryGetValue(cacheKey, out var cached))
                return [.. cached];

            CachedTools[cacheKey] = [.. tools];
            return tools;
        }
    }

    private static string GetCacheKey(string[] enabled)
    {
        return string.Join('\n', enabled
            .Distinct(StringComparer.Ordinal)
            .OrderBy(tool => tool, StringComparer.Ordinal));
    }
}
