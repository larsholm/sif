using System.Text.Json;
using sif.agent;
using Xunit;

namespace sif.agent.tests;

public sealed class GeneralBehaviorTests
{
    [Fact]
    public void ChatMessageNormalizesRoleAndPreservesContent()
    {
        var message = new ChatMessage("Assistant", "hello", "call-1");

        Assert.Equal("assistant", message.Role);
        Assert.Equal("hello", message.Content);
        Assert.Equal("call-1", message.ToolCallId);
    }

    [Fact]
    public void JsonArgsReadsAliasesAndCoercesScalarValues()
    {
        using var doc = JsonDocument.Parse("""
            {
              "filePath": "README.md",
              "limit": "42",
              "timeout": "1.5",
              "enabled": true,
              "tools": ["bash", "read"]
            }
            """);

        var root = doc.RootElement;

        Assert.Equal("README.md", JsonArgs.String(root, "", "path", "filePath"));
        Assert.Equal(42, JsonArgs.Int(root, 0, "limit"));
        Assert.Equal(1.5, JsonArgs.Double(root, 0, "timeout"));
        Assert.Equal("True", JsonArgs.String(root, "", "enabled"));
        Assert.Equal(["bash", "read"], JsonArgs.StringArray(root, "tools"));
    }

    [Fact]
    public void JsonArgsReturnsDefaultsForMissingOrUnsupportedValues()
    {
        using var doc = JsonDocument.Parse("""{"items":[1,2,3],"object":{"x":1}}""");
        var root = doc.RootElement;

        Assert.Equal("fallback", JsonArgs.String(root, "fallback", "missing"));
        Assert.Equal(7, JsonArgs.Int(root, 7, "missing", "object"));
        Assert.Equal(2.5, JsonArgs.Double(root, 2.5, "missing", "items"));
        Assert.Empty(JsonArgs.StringArray(root, "missing"));
    }

    [Fact]
    public void ToolRegistryIncludesRequestedToolsOnly()
    {
        var tools = ToolRegistry.GetTools(["bash", "read"]);
        var names = tools.Select(tool => tool.FunctionName).ToArray();

        Assert.Contains("bash", names);
        Assert.Contains("read", names);
        Assert.DoesNotContain("write", names);
    }

    [Theory]
    [InlineData("bash", "{}", "command is required")]
    [InlineData("read", "{}", "path is required")]
    [InlineData("write", """{"text":"content"}""", "path is required")]
    [InlineData("edit", """{"path":"missing.txt","replacement":"new"}""", "oldText is required")]
    [InlineData("ctx_index", "{}", "content is required")]
    [InlineData("ctx_search", "{}", "query is required")]
    [InlineData("ctx_read", "{}", "id is required")]
    [InlineData("roslyn_find_symbols", "{}", "name is required")]
    public async Task ToolsReturnClearMissingArgumentErrors(string tool, string arguments, string expected)
    {
        var result = await WithTimeout(ToolRegistry.ExecuteAsync(tool, arguments));

        Assert.Contains(expected, result, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("""{"seconds":-1}""", "greater than or equal to 0")]
    [InlineData("""{"seconds":61}""", "less than or equal to 60")]
    [InlineData("""{"timeout":0,"command":"echo nope"}""", "at least 1 second")]
    [InlineData("""{"timeout":601,"command":"echo nope"}""", "at most 600 seconds")]
    public async Task ToolsValidateNumericBounds(string arguments, string expected)
    {
        var tool = arguments.Contains("command", StringComparison.Ordinal) ? "bash" : "sleep";

        var result = await WithTimeout(ToolRegistry.ExecuteAsync(tool, arguments));

        Assert.Contains(expected, result, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<string> WithTimeout(Task<string> task)
    {
        var completed = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(2)));
        if (completed != task)
            throw new TimeoutException("Tool execution did not complete.");

        return await task;
    }
}
