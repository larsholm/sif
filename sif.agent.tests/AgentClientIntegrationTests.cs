using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Spectre.Console;
using sif.agent;
using Xunit;

namespace sif.agent.tests;

public sealed class AgentClientIntegrationTests
{
    [Fact]
    public async Task CompleteAsyncSendsPromptAndExtractsResponseAndReasoning()
    {
        await using var server = new ChatCompletionStub();
        server.Enqueue(ChatResponse("""
            {
              "role": "assistant",
              "content": "plain response",
              "reasoning": "brief reason"
            }
            """));

        var model = ConfiguredDefaultModel();
        var client = new AgentClient(TestConfig(server.BaseUrl, model));

        var (response, reasoning) = await WithTimeout(client.CompleteAsync("hello", "system prompt"));

        Assert.Equal("plain response", response);
        Assert.Equal("brief reason", reasoning);

        var request = server.Requests.Single();
        Assert.Equal("/v1/chat/completions", request.Path);
        Assert.Equal("Bearer test-key", request.Authorization);
        Assert.Equal(model, request.Json.RootElement.GetProperty("model").GetString());

        var messages = request.Json.RootElement.GetProperty("messages").EnumerateArray().ToArray();
        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        Assert.Equal("system prompt", MessageText(messages[0]));
        Assert.Equal("user", messages[1].GetProperty("role").GetString());
        Assert.Equal("hello", MessageText(messages[1]));
    }

    [Fact]
    public async Task CompleteAsyncOmitsAuthorizationForKeylessEndpoint()
    {
        await using var server = new ChatCompletionStub();
        server.Enqueue(ChatResponse("""{"role":"assistant","content":"keyless response"}"""));

        var config = TestConfig(server.BaseUrl, ConfiguredDefaultModel());
        config.ApiKey = null;
        var client = new AgentClient(config);

        var (response, _) = await WithTimeout(client.CompleteAsync("hello", "system prompt"));

        Assert.Equal("keyless response", response);
        Assert.Null(server.Requests.Single().Authorization);
    }

    [Fact]
    public async Task ChatStreamingAsyncDisplaysReasoningDeltasWithoutReturningThemAsAnswerText()
    {
        await using var server = new ChatCompletionStub();
        server.EnqueueStream(
            ChatStreamChunk("{\"role\":\"assistant\",\"reasoning_content\":\"Plan \"}"),
            ChatStreamChunk("{\"reasoning\":\"carefully.\"}"),
            ChatStreamChunk("{\"content\":\"Final \"}"),
            ChatStreamChunk("{\"content\":\"answer.\"}"),
            ChatStreamChunk("{}", "stop"));

        var config = TestConfig(server.BaseUrl, ConfiguredDefaultModel());
        config.ThinkingEnabled = true;
        var client = new AgentClient(config);
        var history = new List<ChatMessage> { new("user", "solve it") };

        var ((response, _), output) = await CaptureConsoleOutputAsync(
            () => WithTimeout(client.ChatStreamingAsync(history)));

        Assert.Equal("Final answer.", response);
        Assert.Contains("Thinking:", output);
        Assert.Contains("Plan carefully.", output);
        Assert.Contains("Final answer.", output);
        Assert.DoesNotContain("Plan", response);
        Assert.DoesNotContain("carefully", response);
        Assert.True(server.Requests.Single().Json.RootElement.GetProperty("stream").GetBoolean());
    }

    [Fact]
    public async Task ChatWithToolsExecutesToolAndContinuesWithToolResult()
    {
        var dir = CreateTempDirectory();
        var file = Path.Combine(dir, "note.txt");
        await File.WriteAllTextAsync(file, "tool result text");

        await using var server = new ChatCompletionStub();
        server.Enqueue(ChatResponse("""
            {
              "role": "assistant",
              "content": null,
              "tool_calls": [
                {
                  "id": "call_read_1",
                  "type": "function",
                  "function": {
                    "name": "read",
                    "arguments": "__ARGS__"
                  }
                }
              ]
            }
            """.Replace("__ARGS__", JsonEncodedText.Encode($$"""{"path":"{{file}}"}""").ToString()), finishReason: "tool_calls"));
        server.Enqueue(ChatResponse("""{"role":"assistant","content":"done"}"""));

        var client = new AgentClient(TestConfig(server.BaseUrl, ConfiguredDefaultModel()), ["read"]);
        var history = new List<ChatMessage> { new("user", "read the note") };
        var steeringComments = new Queue<IReadOnlyList<string>>();
        steeringComments.Enqueue(["also check whether the note is empty"]);

        var (response, tokenCount) = await WithTimeout(client.ChatWithToolsAsync(
            history,
            takeSteeringComments: () => steeringComments.Count > 0 ? steeringComments.Dequeue() : []));

        Assert.Equal("done", response);
        Assert.Equal(492, tokenCount);
        Assert.Equal(2, server.Requests.Count);

        var firstRequest = server.Requests[0].Json.RootElement;
        Assert.True(firstRequest.TryGetProperty("tools", out var tools));
        Assert.Contains(tools.EnumerateArray(), tool =>
            tool.GetProperty("function").GetProperty("name").GetString() == "read");

        var secondMessages = server.Requests[1].Json.RootElement.GetProperty("messages").EnumerateArray().ToArray();
        Assert.Contains(secondMessages, message =>
            message.GetProperty("role").GetString() == "tool" &&
            message.GetProperty("tool_call_id").GetString() == "call_read_1" &&
            MessageText(message).Contains("tool result text", StringComparison.Ordinal));
        Assert.Contains(secondMessages, message =>
            message.GetProperty("role").GetString() == "user" &&
            MessageText(message) == "User steering comment: also check whether the note is empty");

        Assert.Contains(history, message =>
            message.Role == "assistant" &&
            message.Content.Contains("Tool call from prior turn: read", StringComparison.Ordinal) &&
            message.Content.Contains("tool result text", StringComparison.Ordinal));
        Assert.Contains(history, message =>
            message.Role == "user" &&
            message.Content == "User steering comment: also check whether the note is empty");
        Assert.Equal("done", history[^1].Content);

        var snapshot = Assert.IsType<ModelRequestSnapshot>(client.LastRequestSnapshot);
        Assert.Equal(ConfiguredDefaultModel(), snapshot.Model);
        Assert.False(snapshot.Streaming);
        Assert.Contains(snapshot.Tools, tool =>
            tool.Name == "read" &&
            tool.ParametersJson?.Contains("path", StringComparison.Ordinal) == true);
        Assert.Contains(snapshot.Messages, message =>
            message.Role == "assistant" &&
            message.ToolCalls.Any(call => call.Id == "call_read_1" && call.Name == "read"));
        Assert.Contains(snapshot.Messages, message =>
            message.Role == "tool" &&
            message.ToolCallId == "call_read_1" &&
            message.Content.Contains("tool result text", StringComparison.Ordinal));
        Assert.Contains(snapshot.Messages, message =>
            message.Role == "user" &&
            message.Content == "User steering comment: also check whether the note is empty");
        Assert.DoesNotContain(snapshot.Messages, message => message.Content == "done");

        var contextOutput = CaptureConsoleOutput(() =>
            ContextCommandHandler.Handle("/context full", history, client, () => { }));
        Assert.Contains("Last Model Request", contextOutput);
        Assert.Contains("Tool Schemas Sent", contextOutput);
        Assert.Contains("tool result text", contextOutput);
        Assert.DoesNotContain("done", contextOutput);

        var historyOutput = CaptureConsoleOutput(() =>
            ContextCommandHandler.Handle("/context history", history, client, () => { }));
        Assert.Contains("Stored Conversation History", historyOutput);
        Assert.Contains("done", historyOutput);
    }

    [Fact]
    public async Task ChatWithToolsStreamsReasoningAndAccumulatesToolCallDeltas()
    {
        var dir = CreateTempDirectory();
        var file = Path.Combine(dir, "streamed-note.txt");
        await File.WriteAllTextAsync(file, "streamed tool result");
        var arguments = JsonSerializer.Serialize(new { path = file });
        var split = arguments.Length / 2;

        await using var server = new ChatCompletionStub();
        server.EnqueueStream(
            ChatStreamChunk("{\"role\":\"assistant\",\"reasoning_content\":\"Need the file.\"}"),
            ChatStreamChunk(JsonSerializer.Serialize(new
            {
                tool_calls = new[]
                {
                    new
                    {
                        index = 0,
                        id = "call_read_stream",
                        type = "function",
                        function = new { name = "read", arguments = arguments[..split] }
                    }
                }
            })),
            ChatStreamChunk(JsonSerializer.Serialize(new
            {
                tool_calls = new[]
                {
                    new
                    {
                        index = 0,
                        function = new { arguments = arguments[split..] }
                    }
                }
            })),
            ChatStreamChunk("{}", "tool_calls"));
        server.EnqueueStream(
            ChatStreamChunk("{\"role\":\"assistant\",\"reasoning\":\"Now answer.\"}"),
            ChatStreamChunk("{\"content\":\"Done with streamed tools.\"}"),
            ChatStreamChunk("{}", "stop"));

        var config = TestConfig(server.BaseUrl, ConfiguredDefaultModel());
        config.ThinkingEnabled = true;
        var client = new AgentClient(config, ["read"]);
        var history = new List<ChatMessage> { new("user", "read the streamed note") };

        var ((response, _), output) = await CaptureConsoleOutputAsync(
            () => WithTimeout(client.ChatWithToolsAsync(history, streaming: true)));

        Assert.Equal("Done with streamed tools.", response);
        Assert.Contains("Need the file.", output);
        Assert.Contains("Now answer.", output);
        Assert.Contains("streamed tool result", output);
        Assert.Equal(2, server.Requests.Count);
        Assert.All(server.Requests, request =>
            Assert.True(request.Json.RootElement.GetProperty("stream").GetBoolean()));
    }

    [Fact]
    public async Task ChatAsyncCapturesMessagesAndOptionsFromLastModelRequest()
    {
        await using var server = new ChatCompletionStub();
        server.Enqueue(ChatResponse("""
            {"role":"assistant","content":"answer generated afterward"}
            """));

        var config = TestConfig(server.BaseUrl, ConfiguredDefaultModel());
        config.Temperature = 0.25f;
        config.MaxTokens = 321;
        var client = new AgentClient(config);
        var history = new List<ChatMessage>
        {
            new("system", "system prompt"),
            new("user", "question")
        };

        await WithTimeout(client.ChatAsync(history));

        var snapshot = Assert.IsType<ModelRequestSnapshot>(client.LastRequestSnapshot);
        Assert.Equal(0.25f, snapshot.Temperature);
        Assert.Equal(321, snapshot.MaxOutputTokens);
        Assert.Null(snapshot.ReasoningEffort);
        Assert.Empty(snapshot.Tools);
        Assert.Collection(snapshot.Messages,
            message =>
            {
                Assert.Equal("system", message.Role);
                Assert.Equal("system prompt", message.Content);
            },
            message =>
            {
                Assert.Equal("user", message.Role);
                Assert.Equal("question", message.Content);
            });
        Assert.DoesNotContain(snapshot.Messages, message => message.Content.Contains("generated afterward", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ChatWithToolsSendsPriorToolHistoryAsAssistantText()
    {
        await using var server = new ChatCompletionStub();
        server.Enqueue(ChatResponse("""{"role":"assistant","content":"ok"}"""));

        var client = new AgentClient(TestConfig(server.BaseUrl, ConfiguredDefaultModel()), ["read"]);
        var history = new List<ChatMessage>
        {
            new("tool", "legacy tool result", "old-call"),
            new("user", "continue")
        };

        await WithTimeout(client.ChatWithToolsAsync(history));

        var messages = server.Requests.Single().Json.RootElement.GetProperty("messages").EnumerateArray().ToArray();
        Assert.Equal("assistant", messages[0].GetProperty("role").GetString());
        Assert.Equal("Prior tool result:\nlegacy tool result", MessageText(messages[0]));
        Assert.DoesNotContain(messages, message => message.GetProperty("role").GetString() == "tool");
    }

    [Fact]
    public async Task ChatWithToolsSanitizesEmptyToolArgumentsBeforeContinuing()
    {
        await using var server = new ChatCompletionStub();
        server.Enqueue(ChatResponse("""
            {
              "role": "assistant",
              "content": null,
              "tool_calls": [
                {
                  "id": "call_read_empty",
                  "type": "function",
                  "function": {
                    "name": "read",
                    "arguments": ""
                  }
                }
              ]
            }
            """, finishReason: "tool_calls"));
        server.Enqueue(ChatResponse("""{"role":"assistant","content":"recovered"}"""));

        var client = new AgentClient(TestConfig(server.BaseUrl, ConfiguredDefaultModel()), ["read"]);
        var history = new List<ChatMessage> { new("user", "read something") };

        var (response, _) = await WithTimeout(client.ChatWithToolsAsync(history));

        Assert.Equal("recovered", response);
        Assert.Equal(2, server.Requests.Count);

        var secondMessages = server.Requests[1].Json.RootElement.GetProperty("messages").EnumerateArray().ToArray();
        var assistantToolCall = secondMessages.Single(message =>
            message.GetProperty("role").GetString() == "assistant" &&
            message.TryGetProperty("tool_calls", out _));
        var arguments = assistantToolCall
            .GetProperty("tool_calls")[0]
            .GetProperty("function")
            .GetProperty("arguments")
            .GetString();
        Assert.Equal("{}", arguments);

        var toolMessage = secondMessages.Single(message => message.GetProperty("role").GetString() == "tool");
        Assert.Contains("called with empty arguments", MessageText(toolMessage));
        Assert.Contains("path is required", MessageText(toolMessage));
    }

    [Fact]
    public async Task ChatWithToolsRetriesProviderToolParseFailureOnce()
    {
        await using var server = new ChatCompletionStub();
        var parseFailure = """
            {"error":{"code":500,"message":"Failed to parse input at pos 221: </think>\n\n<tool_call>\n<function=bash>\n<parameter=command>\necho bad\n</parameter>\n</function>\n</tool_call>","type":"server_error"}}
            """;
        server.Enqueue(500, parseFailure);
        server.Enqueue(500, parseFailure);
        server.Enqueue(500, parseFailure);
        server.Enqueue(500, parseFailure);
        server.Enqueue(ChatResponse("""{"role":"assistant","content":"recovered"}"""));

        var client = new AgentClient(TestConfig(server.BaseUrl, ConfiguredDefaultModel()), ["bash"]);
        var history = new List<ChatMessage> { new("user", "check shell") };

        var (response, _) = await WithTimeout(client.ChatWithToolsAsync(history));

        Assert.Equal("recovered", response);
        Assert.Equal(5, server.Requests.Count);

        var secondMessages = server.Requests[^1].Json.RootElement.GetProperty("messages").EnumerateArray().ToArray();
        Assert.Contains(secondMessages, message =>
            message.GetProperty("role").GetString() == "system" &&
            MessageText(message).Contains("malformed", StringComparison.OrdinalIgnoreCase) &&
            MessageText(message).Contains("valid JSON object", StringComparison.OrdinalIgnoreCase));

        var snapshot = Assert.IsType<ModelRequestSnapshot>(client.LastRequestSnapshot);
        Assert.Contains(snapshot.Messages, message =>
            message.Role == "system" &&
            message.Content.Contains("malformed", StringComparison.OrdinalIgnoreCase) &&
            message.Content.Contains("valid JSON object", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ChatWithToolsRecoversFromBadGatewayAndKeepsToolProgress()
    {
        var dir = CreateTempDirectory();
        var file = Path.Combine(dir, "progress.txt");
        await File.WriteAllTextAsync(file, "completed tool work");

        await using var server = new ChatCompletionStub();
        server.Enqueue(ChatResponse("""
            {
              "role": "assistant",
              "content": null,
              "tool_calls": [
                {
                  "id": "call_read_before_502",
                  "type": "function",
                  "function": {
                    "name": "read",
                    "arguments": "__ARGS__"
                  }
                }
              ]
            }
            """.Replace("__ARGS__", JsonEncodedText.Encode($$"""{"path":"{{file}}"}""").ToString()), finishReason: "tool_calls"));

        var badGateway = """{"error":{"message":"temporary upstream failure","code":502}}""";
        // The OpenAI SDK makes four attempts before surfacing a 502 to Sif.
        server.Enqueue(502, badGateway);
        server.Enqueue(502, badGateway);
        server.Enqueue(502, badGateway);
        server.Enqueue(502, badGateway);
        server.Enqueue(ChatResponse("""{"role":"assistant","content":"task completed after recovery"}"""));

        var client = new AgentClient(TestConfig(server.BaseUrl, ConfiguredDefaultModel()), ["read"]);
        var history = new List<ChatMessage> { new("user", "read the file and finish the task") };

        var (response, _) = await WithTimeout(client.ChatWithToolsAsync(history));

        Assert.Equal("task completed after recovery", response);
        Assert.Equal(6, server.Requests.Count);
        Assert.Contains(history, message =>
            message.Role == "assistant" &&
            message.Content.Contains("completed tool work", StringComparison.Ordinal));
        Assert.Equal("task completed after recovery", history[^1].Content);

        var recoveredRequest = server.Requests[^1].Json.RootElement;
        var recoveredMessages = recoveredRequest.GetProperty("messages").EnumerateArray().ToArray();
        Assert.Contains(recoveredMessages, message =>
            message.GetProperty("role").GetString() == "tool" &&
            MessageText(message).Contains("completed tool work", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ChatAsyncRetriesEmbeddedOpenRouterProviderError()
    {
        await using var server = new ChatCompletionStub();
        server.Enqueue(200, OpenRouterErrorResponse(
            502,
            "Provider disconnected during generation",
            "provider_unavailable"));
        server.Enqueue(ChatResponse("""{"role":"assistant","content":"recovered response"}"""));

        var client = new AgentClient(TestConfig(server.BaseUrl, ConfiguredDefaultModel()));
        var history = new List<ChatMessage> { new("user", "continue") };

        var (response, _) = await WithTimeout(client.ChatAsync(history));

        Assert.Equal("recovered response", response);
        Assert.Equal(2, server.Requests.Count);
    }

    [Fact]
    public async Task ChatAsyncSurfacesEmbeddedOpenRouterErrorDetails()
    {
        await using var server = new ChatCompletionStub();
        server.Enqueue(200, OpenRouterErrorResponse(
            402,
            "Insufficient credits for this request",
            "payment_required"));

        var client = new AgentClient(TestConfig(server.BaseUrl, ConfiguredDefaultModel()));
        var history = new List<ChatMessage> { new("user", "continue") };

        var exception = await Assert.ThrowsAsync<System.ClientModel.ClientResultException>(
            () => WithTimeout(client.ChatAsync(history)));

        var userMessage = AgentErrorFormatter.ToUserMessage(exception);
        Assert.Contains("billing or quota", userMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Insufficient credits", userMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("Unknown ChatFinishReason", userMessage, StringComparison.Ordinal);
        Assert.Single(server.Requests);
    }

    private static AgentConfig TestConfig(string baseUrl, string model)
    {
        return new AgentConfig
        {
            BaseUrl = baseUrl,
            ApiKey = "test-key",
            Model = model,
            ThinkingEnabled = false
        };
    }

    private static string ConfiguredDefaultModel()
    {
        var config = AgentConfig.Load();
        return config.Profiles.TryGetValue("default", out var profile) && !string.IsNullOrWhiteSpace(profile.Model)
            ? profile.Model
            : config.Model;
    }

    private static async Task<T> WithTimeout<T>(Task<T> task)
    {
        var completed = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(5)));
        if (completed != task)
            throw new TimeoutException("Agent client call did not complete.");

        return await task;
    }

    private static string ChatResponse(string messageJson, string finishReason = "stop")
    {
        return $$"""
            {
              "id": "chatcmpl-test",
              "object": "chat.completion",
              "created": 1,
              "model": "test-model",
              "choices": [
                {
                  "index": 0,
                  "message": {{messageJson}},
                  "finish_reason": "{{finishReason}}"
                }
              ],
              "usage": {
                "prompt_tokens": 123,
                "completion_tokens": 123,
                "total_tokens": 246
              }
            }
            """;
    }

    private static string ChatStreamChunk(string deltaJson, string? finishReason = null)
    {
        var serializedFinishReason = finishReason is null ? "null" : JsonSerializer.Serialize(finishReason);
        return $$"""
            {
              "id": "chatcmpl-test",
              "object": "chat.completion.chunk",
              "created": 1,
              "model": "test-model",
              "choices": [
                {
                  "index": 0,
                  "delta": {{deltaJson}},
                  "finish_reason": {{serializedFinishReason}}
                }
              ]
            }
            """;
    }

    private static string OpenRouterErrorResponse(int code, string message, string errorType)
    {
        return $$"""
            {
              "id": "chatcmpl-error",
              "object": "chat.completion",
              "created": 1,
              "model": "test-model",
              "choices": [
                {
                  "index": 0,
                  "message": {"role":"assistant","content":"partial output"},
                  "finish_reason": "error",
                  "error": {
                    "code": {{code}},
                    "message": "{{message}}",
                    "metadata": {"error_type":"{{errorType}}"}
                  }
                }
              ]
            }
            """;
    }

    private static string MessageText(JsonElement message)
    {
        var content = message.GetProperty("content");
        if (content.ValueKind == JsonValueKind.String)
            return content.GetString() ?? "";

        if (content.ValueKind == JsonValueKind.Array)
        {
            var sb = new StringBuilder();
            foreach (var part in content.EnumerateArray())
            {
                if (part.TryGetProperty("text", out var text))
                    sb.Append(text.GetString());
            }
            return sb.ToString();
        }

        return content.ToString();
    }

    private static string CreateTempDirectory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sif-agent-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string CaptureConsoleOutput(Action action)
    {
        var original = AnsiConsole.Console;
        using var writer = new StringWriter();
        AnsiConsole.Console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(writer)
        });

        try
        {
            action();
            return StripAnsi(writer.ToString());
        }
        finally
        {
            AnsiConsole.Console = original;
        }
    }

    private static async Task<(T Result, string Output)> CaptureConsoleOutputAsync<T>(Func<Task<T>> action)
    {
        var original = AnsiConsole.Console;
        using var writer = new StringWriter();
        AnsiConsole.Console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(writer)
        });

        try
        {
            var result = await action();
            return (result, StripAnsi(writer.ToString()));
        }
        finally
        {
            AnsiConsole.Console = original;
        }
    }

    private static string StripAnsi(string text) =>
        Regex.Replace(text, "\u001B\\[[0-?]*[ -/]*[@-~]", "");

    private sealed class ChatCompletionStub : IAsyncDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly Queue<StubResponse> _responses = new();
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _loop;

        public string BaseUrl { get; }
        public List<CapturedRequest> Requests { get; } = new();

        public ChatCompletionStub()
        {
            var port = GetFreePort();
            BaseUrl = $"http://127.0.0.1:{port}/v1";
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            _listener.Start();
            _loop = Task.Run(ListenAsync);
        }

        public void Enqueue(string responseJson)
        {
            Enqueue(responseJson.Contains("\"error\"", StringComparison.Ordinal) ? 500 : 200, responseJson);
        }

        public void Enqueue(int statusCode, string responseJson)
        {
            _responses.Enqueue(new StubResponse(statusCode, responseJson, "application/json"));
        }

        public void EnqueueStream(params string[] responseJsonChunks)
        {
            var body = string.Join("", responseJsonChunks.Select(chunk =>
                $"data: {chunk.Replace("\r", "").Replace("\n", "")}\n\n")) + "data: [DONE]\n\n";
            _responses.Enqueue(new StubResponse(200, body, "text/event-stream"));
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            _listener.Stop();

            try { await _loop; }
            catch (HttpListenerException) { }
            catch (ObjectDisposedException) { }

            _listener.Close();
            _cts.Dispose();
        }

        private async Task ListenAsync()
        {
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    var context = await _listener.GetContextAsync();
                    await HandleAsync(context);
                }
            }
            catch (HttpListenerException) when (_cts.IsCancellationRequested)
            {
            }
            catch (ObjectDisposedException) when (_cts.IsCancellationRequested)
            {
            }
        }

        private async Task HandleAsync(HttpListenerContext context)
        {
            string body;
            using (var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding))
                body = await reader.ReadToEndAsync();

            Requests.Add(new CapturedRequest(
                context.Request.Url?.AbsolutePath ?? "",
                JsonDocument.Parse(body),
                context.Request.Headers["Authorization"]));

            var response = _responses.Count > 0
                ? _responses.Dequeue()
                : new StubResponse(500, """{"error":{"message":"No stub response queued"}}""", "application/json");

            context.Response.StatusCode = response.StatusCode;
            context.Response.ContentType = response.ContentType;

            var bytes = Encoding.UTF8.GetBytes(response.Body);
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes);
            context.Response.Close();
        }

        private static int GetFreePort()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
    }

    private sealed record CapturedRequest(string Path, JsonDocument Json, string? Authorization);
    private sealed record StubResponse(int StatusCode, string Body, string ContentType);
}
