using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
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
        Assert.Equal(model, request.Json.RootElement.GetProperty("model").GetString());

        var messages = request.Json.RootElement.GetProperty("messages").EnumerateArray().ToArray();
        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        Assert.Equal("system prompt", MessageText(messages[0]));
        Assert.Equal("user", messages[1].GetProperty("role").GetString());
        Assert.Equal("hello", MessageText(messages[1]));
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

        var (response, tokenCount) = await WithTimeout(client.ChatWithToolsAsync(history));

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

        Assert.Contains(history, message =>
            message.Role == "assistant" &&
            message.Content.Contains("Tool call from prior turn: read", StringComparison.Ordinal) &&
            message.Content.Contains("tool result text", StringComparison.Ordinal));
        Assert.Equal("done", history[^1].Content);
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

    private sealed class ChatCompletionStub : IAsyncDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly Queue<string> _responses = new();
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
            _responses.Enqueue(responseJson);
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

            Requests.Add(new CapturedRequest(context.Request.Url?.AbsolutePath ?? "", JsonDocument.Parse(body)));

            var response = _responses.Count > 0
                ? _responses.Dequeue()
                : """{"error":{"message":"No stub response queued"}}""";

            context.Response.StatusCode = response.Contains("\"error\"", StringComparison.Ordinal) ? 500 : 200;
            context.Response.ContentType = "application/json";

            var bytes = Encoding.UTF8.GetBytes(response);
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

    private sealed record CapturedRequest(string Path, JsonDocument Json);
}
