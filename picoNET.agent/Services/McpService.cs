using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using OpenAI.Chat;
using Spectre.Console;

namespace picoNET.agent;

/// <summary>
/// Manages connections to MCP (Model Context Protocol) servers.
/// </summary>
internal class McpService : IDisposable, IAsyncDisposable
{
    private const int MaxInitializationAttempts = 5;
    private const int MaxToolCallAttempts = 4;
    private static readonly TimeSpan InitializationAttemptTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ToolCallAttemptTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan[] InitializationRetryDelays =
    [
        TimeSpan.FromMilliseconds(250),
        TimeSpan.FromMilliseconds(500),
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2)
    ];
    private static readonly TimeSpan[] ToolCallRetryDelays =
    [
        TimeSpan.FromMilliseconds(250),
        TimeSpan.FromMilliseconds(750),
        TimeSpan.FromSeconds(1.5)
    ];

    private readonly Dictionary<string, McpClient> _clients = new();
    private readonly List<ChatTool> _mcpTools = new();
    private readonly Dictionary<string, string> _toolToClientMap = new();

    public async Task InitializeAsync(Dictionary<string, McpServerConfig> configs)
    {
        foreach (var (name, config) in configs)
        {
            try
            {
                if (config.Disabled)
                    continue;

                await InitializeServerWithRetryAsync(name, config);
            }
            catch (OperationCanceledException)
            {
                AnsiConsole.MarkupLine($"[red]Error: MCP server '{name}' timed out during initialization.[/]");
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[yellow]Warning: Failed to connect to MCP server '{name}': {ex.Message}[/]");
            }
        }
    }

    private async Task InitializeServerWithRetryAsync(string name, McpServerConfig config)
    {
        Exception? lastError = null;

        for (var attempt = 1; attempt <= MaxInitializationAttempts; attempt++)
        {
            try
            {
                using var cts = new CancellationTokenSource(InitializationAttemptTimeout);
                var transport = CreateTransport(name, config);
                var client = await McpClient.CreateAsync(transport, new McpClientOptions
                {
                    ClientInfo = new Implementation { Name = "picoNET.agent", Version = "1.0.0" }
                }, cancellationToken: cts.Token);

                _clients[name] = client;

                var tools = await client.ListToolsAsync(cancellationToken: cts.Token);
                foreach (var tool in tools)
                {
                    // Map MCP tool to OpenAI ChatTool
                    var chatTool = ChatTool.CreateFunctionTool(
                        tool.Name,
                        tool.Description,
                        BinaryData.FromString(JsonSerializer.Serialize(tool.ProtocolTool.InputSchema))
                    );

                    _mcpTools.Add(chatTool);
                    _toolToClientMap[tool.Name] = name;
                }

                if (attempt > 1)
                    AnsiConsole.MarkupLine($"[dim]Connected to MCP server '{name}' after {attempt} attempts.[/]");
                return;
            }
            catch (OperationCanceledException ex)
            {
                lastError = ex;
            }
            catch (Exception ex)
            {
                lastError = ex;
            }

            if (attempt < MaxInitializationAttempts)
                await Task.Delay(InitializationRetryDelays[attempt - 1]);
        }

        if (lastError is OperationCanceledException)
            throw lastError;

        throw new InvalidOperationException(lastError?.Message ?? "unknown initialization error", lastError);
    }

    private static IClientTransport CreateTransport(string name, McpServerConfig config)
    {
        if (string.Equals(config.Type, "stdio", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(config.Type))
        {
            if (string.IsNullOrWhiteSpace(config.Command))
                throw new InvalidOperationException("missing command");

            var transportOptions = new StdioClientTransportOptions
            {
                Name = name,
                Command = config.Command,
                Arguments = config.Args,
                EnvironmentVariables = config.Env?.ToDictionary(k => k.Key, v => (string?)v.Value)
            };

            return new StdioClientTransport(transportOptions);
        }

        if (string.Equals(config.Type, "streamableHttp", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(config.Type, "streamable-http", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(config.Type, "http", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(config.Type, "sse", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(config.Url))
                throw new InvalidOperationException("missing url");

            var transportMode = config.Type.ToLowerInvariant() switch
            {
                "streamablehttp" or "streamable-http" => HttpTransportMode.StreamableHttp,
                "sse" => HttpTransportMode.Sse,
                _ => HttpTransportMode.AutoDetect
            };

            var transportOptions = new HttpClientTransportOptions
            {
                Name = name,
                Endpoint = new Uri(config.Url),
                TransportMode = transportMode,
                AdditionalHeaders = config.Headers
            };

            return new HttpClientTransport(transportOptions);
        }

        throw new InvalidOperationException($"unsupported MCP transport type '{config.Type}'");
    }

    public List<ChatTool> GetTools() => _mcpTools;

    public async Task<string> ExecuteToolAsync(string toolName, string argumentsJson)
    {
        if (!_toolToClientMap.TryGetValue(toolName, out var clientName))
            return $"Error: Tool '{toolName}' not found in any MCP server.";

        var client = _clients[clientName];
        Exception? lastError = null;

        for (var attempt = 1; attempt <= MaxToolCallAttempts; attempt++)
        {
            try
            {
                using var cts = new CancellationTokenSource(ToolCallAttemptTimeout);
                var arguments = JsonSerializer.Deserialize<Dictionary<string, object?>>(argumentsJson);
                var result = await client.CallToolAsync(toolName, arguments, cancellationToken: cts.Token);

                // MCP results can have multiple content items (text, image, etc.)
                var textResults = result.Content
                    .OfType<TextContentBlock>()
                    .Select(c => c.Text);

                var text = string.Join("\n", textResults);
                if (attempt > 1)
                    return $"(MCP tool '{toolName}' succeeded after {attempt} attempts.)\n{text}";

                return text;
            }
            catch (OperationCanceledException ex)
            {
                lastError = ex;
            }
            catch (Exception ex)
            {
                lastError = ex;
            }

            if (attempt < MaxToolCallAttempts)
                await Task.Delay(ToolCallRetryDelays[attempt - 1]);
        }

        if (lastError is OperationCanceledException)
            return $"Error: MCP tool '{toolName}' timed out after {MaxToolCallAttempts} attempts.";

        return $"Error executing MCP tool '{toolName}' after {MaxToolCallAttempts} attempts: {lastError?.Message ?? "unknown error"}";
    }

    public void Dispose()
    {
        // Fire and forget disposal if called synchronously
        _ = DisposeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var client in _clients.Values)
        {
            try { await client.DisposeAsync(); } catch { }
        }
        _clients.Clear();
    }
}
