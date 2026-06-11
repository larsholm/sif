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

    [Fact]
    public void AgentConfigAppliesModelTimeoutValue()
    {
        var config = new AgentConfig();

        config.ApplyValue("MODEL_TIMEOUT_SECONDS", "300");

        Assert.Equal(300, config.ModelTimeoutSeconds);
    }

    [Fact]
    public void CliParserReadsModelTimeoutAliases()
    {
        var opts = CliParser.ParseArgs(["--timeout", "240"]);
        var aliasOpts = CliParser.ParseArgs(["--model-timeout=300"]);

        Assert.Equal(240, opts.ModelTimeoutSeconds);
        Assert.Equal(300, aliasOpts.ModelTimeoutSeconds);
    }

    [Fact]
    public void AgentConfigMigratesLegacyProfilesToProviders()
    {
        var config = JsonSerializer.Deserialize<AgentConfig>("""
            {
              "Profiles": {
                "default": {
                  "Name": "default",
                  "BaseUrl": "http://localhost:11434/v1/",
                  "ApiKey": "test-key",
                  "Model": "llama3.2",
                  "ModelTimeoutSeconds": 240,
                  "UseSecureApiKeyStorage": true
                }
              },
              "CurrentProfile": "default"
            }
            """, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        Assert.NotNull(config);
        Assert.True(AgentConfig.NormalizeProfiles(config));
        Assert.True(config.SwitchProfile("default"));

        Assert.Equal("http://localhost:11434/v1", config.BaseUrl);
        Assert.Equal("test-key", config.ApiKey);
        Assert.Equal("llama3.2", config.Model);
        Assert.Equal(240, config.ModelTimeoutSeconds);
        Assert.True(config.UseSecureApiKeyStorage);
        Assert.True(config.Providers.ContainsKey("default"));
        Assert.Null(config.Profiles["default"].BaseUrl);
    }

    [Fact]
    public void AgentConfigSwitchProfileUsesProviderSettings()
    {
        var config = new AgentConfig
        {
            Providers =
            {
                ["local"] = new ProviderConfig
                {
                    Name = "local",
                    BaseUrl = "http://localhost:8020/v1",
                    ApiKey = "provider-key",
                    TimeoutSeconds = 300
                }
            },
            Profiles =
            {
                ["qwen"] = new ModelProfile
                {
                    Name = "qwen",
                    Provider = "local",
                    Model = "qwen3.6",
                    Temperature = 0.2f
                }
            }
        };

        Assert.True(config.SwitchProfile("qwen"));

        Assert.Equal("http://localhost:8020/v1", config.BaseUrl);
        Assert.Equal("provider-key", config.ApiKey);
        Assert.Equal("qwen3.6", config.Model);
        Assert.Equal(0.2f, config.Temperature);
        Assert.Equal(300, config.ModelTimeoutSeconds);
    }

    [Fact]
    public void AgentConfigMigratesProviderModelTimeoutAlias()
    {
        var config = JsonSerializer.Deserialize<AgentConfig>("""
            {
              "Providers": {
                "default": {
                  "Name": "default",
                  "BaseUrl": "http://localhost:8020/v1",
                  "ModelTimeoutSeconds": 600
                }
              },
              "Profiles": {
                "default": {
                  "Name": "default",
                  "Provider": "default",
                  "Model": "qwen3.6"
                }
              },
              "CurrentProfile": "default"
            }
            """, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        Assert.NotNull(config);
        Assert.True(AgentConfig.NormalizeProfiles(config));
        Assert.True(config.SwitchProfile("default"));

        Assert.Equal(600, config.Providers["default"].TimeoutSeconds);
        Assert.Null(config.Providers["default"].ModelTimeoutSeconds);
        Assert.Equal(600, config.ModelTimeoutSeconds);
    }

    [Fact]
    public void AgentConfigSwitchProfileKeepsExistingGlobalTimeoutWhenProfileHasNone()
    {
        var config = new AgentConfig
        {
            ModelTimeoutSeconds = 300,
            Providers =
            {
                ["local"] = new ProviderConfig
                {
                    Name = "local",
                    BaseUrl = "http://localhost:8020/v1"
                }
            },
            Profiles =
            {
                ["qwen"] = new ModelProfile
                {
                    Name = "qwen",
                    Provider = "local",
                    Model = "qwen3.6"
                }
            }
        };

        Assert.True(config.SwitchProfile("qwen"));

        Assert.Equal(300, config.ModelTimeoutSeconds);
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
