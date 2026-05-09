using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using OpenAI.Chat;

namespace picoNET.agent;

/// <summary>
/// Manages connections to MCP (Model Context Protocol) servers.
/// </summary>
internal class McpService : IDisposable, IAsyncDisposable
{
    private readonly Dictionary<string, McpClient> _clients = new();
    private readonly List<ChatTool> _mcpTools = new();
    private readonly Dictionary<string, string> _toolToClientMap = new();

    public async Task InitializeAsync(Dictionary<string, McpServerConfig> configs)
    {
        foreach (var (name, config) in configs)
        {
            try
            {
                var transportOptions = new StdioClientTransportOptions
                {
                    Command = config.Command,
                    Arguments = config.Args,
                    EnvironmentVariables = config.Env?.ToDictionary(k => k.Key, v => (string?)v.Value)
                };

                var transport = new StdioClientTransport(transportOptions);
                var client = await McpClient.CreateAsync(transport, new McpClientOptions
                {
                    ClientInfo = new Implementation { Name = "picoNET.agent", Version = "1.0.0" }
                });

                _clients[name] = client;

                var tools = await client.ListToolsAsync();
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
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[yellow]Warning: Failed to connect to MCP server '{name}': {ex.Message}[/]");
            }
        }
    }

    public List<ChatTool> GetTools() => _mcpTools;

    public async Task<string> ExecuteToolAsync(string toolName, string argumentsJson)
    {
        if (!_toolToClientMap.TryGetValue(toolName, out var clientName))
            return $"Error: Tool '{toolName}' not found in any MCP server.";

        var client = _clients[clientName];
        try
        {
            var arguments = JsonSerializer.Deserialize<Dictionary<string, object?>>(argumentsJson);
            var result = await client.CallToolAsync(toolName, arguments);
            
            // MCP results can have multiple content items (text, image, etc.)
            var textResults = result.Content
                .OfType<TextContentBlock>()
                .Select(c => c.Text);
            
            return string.Join("\n", textResults);
        }
        catch (Exception ex)
        {
            return $"Error executing MCP tool '{toolName}': {ex.Message}";
        }
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
