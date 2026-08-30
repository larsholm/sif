using System.Diagnostics;
using System.Text.Json;
using sif.agent;
using sif.agent.Services;
using Xunit;

namespace sif.agent.tests;

public sealed class GeneralBehaviorTests
{
    [Theory]
    [InlineData(32768, 19660)]
    [InlineData(100000, 60000)]
    [InlineData(400000, AgentConfig.DefaultCompactionThreshold)]
    public void AutomaticCompactionThresholdUsesSmallerOfDefaultAndSixtyPercent(
        int modelContextLength,
        int expected)
    {
        Assert.Equal(expected, AgentApp.CalculateAutomaticCompactionThreshold(modelContextLength));
    }

    [Fact]
    public void NativeLmStudioMetadataUsesLoadedInstanceContextInsteadOfModelMaximum()
    {
        using var document = JsonDocument.Parse("""
            {
              "models": [
                {
                  "key": "qwen-local",
                  "max_context_length": 262144,
                  "loaded_instances": [
                    {
                      "id": "qwen-local",
                      "config": { "context_length": 160000 }
                    }
                  ]
                }
              ]
            }
            """);

        var info = AgentApp.TryReadModelEndpointInfo(document.RootElement, "qwen-local");

        Assert.NotNull(info);
        Assert.Equal(160000, info.ContextLength);
    }

    [Fact]
    public void LegacyLmStudioMetadataUsesLoadedContextLength()
    {
        using var document = JsonDocument.Parse("""
            {
              "data": [
                {
                  "id": "qwen-local",
                  "max_context_length": 262144,
                  "loaded_context_length": 120000
                }
              ]
            }
            """);

        var info = AgentApp.TryReadModelEndpointInfo(document.RootElement, "qwen-local");

        Assert.NotNull(info);
        Assert.Equal(120000, info.ContextLength);
    }

    [Fact]
    public async Task ModelMetadataProbeUsesOneShortDeadlineForAllCandidates()
    {
        var requestCount = 0;
        using var http = new HttpClient(new StubHttpHandler(async (_, cancellationToken) =>
        {
            requestCount++;
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("The cancellation token should end the request.");
        }))
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        var stopwatch = Stopwatch.StartNew();

        var info = await AgentApp.FetchModelInfoAsync(
            "http://unresponsive-model-host:1234/v1",
            "test-model",
            http,
            TimeSpan.FromMilliseconds(50));

        stopwatch.Stop();
        Assert.Null(info.ContextLength);
        Assert.Null(info.OutputPricePerMillion);
        Assert.Equal(1, requestCount);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void RequestBudgetIncludesToolSchemasAndReservesOutputContext()
    {
        var config = new AgentConfig
        {
            BaseUrl = "http://localhost:1234/v1",
            Model = "test-model",
            MaxTokens = null
        };
        var history = new List<ChatMessage>
        {
            new("system", "system"),
            new("user", new string('x', 3000))
        };

        var withoutTools = new AgentClient(config).EstimateRequestBudget(history, 160000);
        var withTools = new AgentClient(config, ["read", "edit"]).EstimateRequestBudget(history, 160000);

        Assert.True(withTools.ApproximateInputCharacters > withoutTools.ApproximateInputCharacters);
        Assert.True(withTools.EstimatedInputTokens > withoutTools.EstimatedInputTokens);
        Assert.Equal(24000, withTools.ReservedOutputTokens);
        Assert.Equal(136000, withTools.AvailableInputTokens);
    }

    private sealed class StubHttpHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handle) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => handle(request, cancellationToken);
    }

    [Fact]
    public void ConversationStorePersistsAndRestoresConversationHistory()
    {
        var root = Path.Combine(Path.GetTempPath(), "sif-conversations-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = ConversationStore.Create(root, "test-model");
            var history = new List<ChatMessage>
            {
                new("system", "Be concise."),
                new("user", "Remember this task."),
                new("assistant", "I will.")
            };

            store.Save(history);
            var goal = new ConversationGoal(
                "All focused tests pass.",
                DateTimeOffset.UtcNow.ToString("O"),
                EvaluatedTurns: 2,
                LastReason: "One test remains.");
            store.SetGoal(goal);

            var summary = Assert.Single(ConversationStore.List(root));
            Assert.Equal(store.Session.Id, summary.Id);
            Assert.Equal("active", summary.Status);
            Assert.Equal(3, summary.MessageCount);
            Assert.Equal("Remember this task.", summary.Preview);
            Assert.Equal("test-model", summary.Model);
            Assert.True(summary.HasUserMessages);
            Assert.Equal(goal, summary.Goal);
            Assert.Equal(Path.TrimEndingDirectorySeparator(Path.GetFullPath(Environment.CurrentDirectory)), summary.WorkingDirectory);

            Assert.True(ConversationStore.TryOpen(root, store.Session.Id[5..], out var reopened, out var restored, out var error), error);
            Assert.NotNull(reopened);
            Assert.Equal(goal, reopened!.Session.Goal);
            Assert.Equal(history.Select(message => (message.Role, message.Content)), restored!.Select(message => (message.Role, message.Content)));

            reopened.Close();
            Assert.Equal("closed", Assert.Single(ConversationStore.List(root)).Status);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ConversationRecoveryOnlyFindsActiveChatsFromTheSameFolder()
    {
        var root = Path.Combine(Path.GetTempPath(), "sif-conversations-" + Guid.NewGuid().ToString("N"));
        var currentFolder = Path.Combine(root, "project-a");
        var otherFolder = Path.Combine(root, "project-b");
        try
        {
            var matching = ConversationStore.Create(root, null, currentFolder);
            matching.Save([new ChatMessage("user", "Current project task")]);
            var other = ConversationStore.Create(root, null, otherFolder);
            other.Save([new ChatMessage("user", "Other project task")]);

            var found = ConversationStore.FindMostRecentActive(root, currentFolder + Path.DirectorySeparatorChar);

            Assert.NotNull(found);
            Assert.Equal(matching.Session.Id, found.Id);
            Assert.Null(ConversationStore.FindMostRecentActive(root, Path.Combine(root, "project-c")));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ConversationRecoveryIgnoresLegacyChatsWithoutFolderMetadata()
    {
        var root = Path.Combine(Path.GetTempPath(), "sif-conversations-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = ConversationStore.Create(root, null, Path.Combine(root, "project"));
            store.Save([new ChatMessage("user", "A legacy project task")]);
            var metadataPath = Path.Combine(root, store.Session.Id, "session.json");
            var metadata = File.ReadAllText(metadataPath).Replace(
                $",\"WorkingDirectory\":{JsonSerializer.Serialize(store.Session.WorkingDirectory)}",
                "");
            File.WriteAllText(metadataPath, metadata);

            Assert.Null(ConversationStore.FindMostRecentActive(root, Path.Combine(root, "project")));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ConversationListFiltersConfigOnlySessionsAndMigratesLegacyMetadata()
    {
        var root = Path.Combine(Path.GetTempPath(), "sif-conversations-" + Guid.NewGuid().ToString("N"));
        try
        {
            var configOnly = ConversationStore.Create(root, "test-model");
            configOnly.Save([new ChatMessage("system", "System configuration")]);
            var resumable = ConversationStore.Create(root, "test-model");
            resumable.Save([
                new ChatMessage("system", "System configuration"),
                new ChatMessage("user", "A real task")
            ]);

            var configOnlyMetadataPath = Path.Combine(root, configOnly.Session.Id, "session.json");
            var resumableMetadataPath = Path.Combine(root, resumable.Session.Id, "session.json");
            File.WriteAllText(configOnlyMetadataPath, File.ReadAllText(configOnlyMetadataPath)
                .Replace(",\"HasUserMessages\":false", "", StringComparison.Ordinal));
            File.WriteAllText(resumableMetadataPath, File.ReadAllText(resumableMetadataPath)
                .Replace(",\"HasUserMessages\":true", "", StringComparison.Ordinal));

            var listed = Assert.Single(ConversationStore.List(root));

            Assert.Equal(resumable.Session.Id, listed.Id);
            Assert.True(listed.HasUserMessages);
            using var configOnlyMetadata = JsonDocument.Parse(File.ReadAllText(configOnlyMetadataPath));
            using var resumableMetadata = JsonDocument.Parse(File.ReadAllText(resumableMetadataPath));
            Assert.False(configOnlyMetadata.RootElement.GetProperty("HasUserMessages").GetBoolean());
            Assert.True(resumableMetadata.RootElement.GetProperty("HasUserMessages").GetBoolean());
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ConversationListUsesMetadataWithoutLoadingHistory()
    {
        var root = Path.Combine(Path.GetTempPath(), "sif-conversations-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = ConversationStore.Create(root, null);
            store.Save([new ChatMessage("user", "Saved message")]);
            File.Delete(Path.Combine(root, store.Session.Id, "history.json"));

            var summary = Assert.Single(ConversationStore.List(root));
            Assert.Equal(store.Session.Id, summary.Id);
            Assert.False(ConversationStore.TryOpen(root, store.Session.Id, out _, out _, out var error));
            Assert.Contains("no history file", error, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

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
    public void AgentConfigApplyValueUpdatesTheActiveProviderAndProfile()
    {
        var config = new AgentConfig();
        AgentConfig.NormalizeProfiles(config);

        config.ApplyValue("BASE_URL", "http://localhost:9000/v1/");
        config.ApplyValue("MODEL", "updated-model");
        config.ApplyValue("MODEL_TIMEOUT_SECONDS", "450");
        config.ApplyValue("THINKING_ENABLED", "false");

        var provider = config.Providers["default"];
        var profile = config.Profiles["default"];
        Assert.Equal("http://localhost:9000/v1", provider.BaseUrl);
        Assert.Equal(450, provider.TimeoutSeconds);
        Assert.Equal("updated-model", profile.Model);
        Assert.False(profile.ThinkingEnabled);
    }

    [Fact]
    public void NewAgentConfigSerializesEveryAvailableSettingWithDefaults()
    {
        var config = new AgentConfig();
        AgentConfig.NormalizeProfiles(config);

        using var document = JsonDocument.Parse(config.ToFileJson());
        var root = document.RootElement;
        string[] expectedSettings =
        [
            "Tools",
            "ShellAllowedCommands",
            "AutoUpdateEnabled",
            "AutoUpdateSource",
            "Providers",
            "Profiles",
            "CurrentProfile",
            "CompactionThreshold",
            "McpServers"
        ];

        Assert.Equal(expectedSettings, root.EnumerateObject().Select(property => property.Name));

        Assert.Equal(JsonValueKind.Null, root.GetProperty("AutoUpdateSource").ValueKind);
        Assert.Equal(
            AgentConfig.CreateDefaultTools(),
            root.GetProperty("Tools").EnumerateArray().Select(item => item.GetString()));
        Assert.Empty(root.GetProperty("ShellAllowedCommands").EnumerateArray());
        Assert.False(root.GetProperty("AutoUpdateEnabled").GetBoolean());
        Assert.Equal(AgentConfig.DefaultCompactionThreshold, root.GetProperty("CompactionThreshold").GetInt32());

        var provider = root.GetProperty("Providers").GetProperty("default");
        var profile = root.GetProperty("Profiles").GetProperty("default");
        Assert.False(provider.GetProperty("UseSecureApiKeyStorage").GetBoolean());
        Assert.True(profile.GetProperty("ThinkingEnabled").GetBoolean());
    }

    [Fact]
    public void AgentConfigMigratesFlatAndValuesSettingsIntoActiveProviderAndProfile()
    {
        const string json = """
            {
              "BaseUrl": "http://localhost:8020/v1/",
              "Model": "new-model",
              "Temperature": 0.25,
              "ModelTimeoutSeconds": 300,
              "ThinkingEnabled": false,
              "Providers": {
                "local": {
                  "Name": "wrong-name",
                  "BaseUrl": "http://old-host/v1"
                }
              },
              "Profiles": {
                "active": {
                  "Name": "wrong-name",
                  "Provider": "local",
                  "Model": "old-model"
                }
              },
              "CurrentProfile": "active",
              "Values": {
                "SHELL_ALLOWED_COMMANDS": "dotnet,git"
              }
            }
            """;
        var config = JsonSerializer.Deserialize<AgentConfig>(json)!;

        Assert.True(AgentConfig.MigrateLegacyFormat(config, json));

        var provider = config.Providers["local"];
        var profile = config.Profiles["active"];
        Assert.Equal("local", provider.Name);
        Assert.Equal("http://localhost:8020/v1", provider.BaseUrl);
        Assert.Equal(300, provider.TimeoutSeconds);
        Assert.Equal("active", profile.Name);
        Assert.Equal("new-model", profile.Model);
        Assert.Equal(0.25f, profile.Temperature);
        Assert.False(profile.ThinkingEnabled);
        Assert.Equal(["dotnet", "git"], config.ShellAllowedCommands!);
        Assert.Empty(config.Values);

        using var migratedDocument = JsonDocument.Parse(config.ToFileJson());
        Assert.False(migratedDocument.RootElement.TryGetProperty("BaseUrl", out _));
        Assert.False(migratedDocument.RootElement.TryGetProperty("Model", out _));
        Assert.False(migratedDocument.RootElement.TryGetProperty("Values", out _));
    }

    [Fact]
    public void AgentConfigMigratesFlatOnlyConfigToDefaultProviderAndProfile()
    {
        const string json = """
            {
              "BaseUrl": "http://localhost:11434/v1",
              "ApiKey": "local-key",
              "Model": "llama3.2",
              "MaxTokens": 2048,
              "ModelTimeoutSeconds": 120
            }
            """;
        var config = JsonSerializer.Deserialize<AgentConfig>(json)!;

        Assert.True(AgentConfig.MigrateLegacyFormat(config, json));

        Assert.Equal("default", config.CurrentProfile);
        Assert.Equal("http://localhost:11434/v1", config.Providers["default"].BaseUrl);
        Assert.Equal("local-key", config.Providers["default"].ApiKey);
        Assert.Equal(120, config.Providers["default"].TimeoutSeconds);
        Assert.Equal("llama3.2", config.Profiles["default"].Model);
        Assert.Equal(2048, config.Profiles["default"].MaxTokens);
    }

    [Fact]
    public void FirstRunConfigIncludesACompleteDefaultProviderAndProfile()
    {
        var config = new AgentConfig();

        Assert.True(AgentConfig.NormalizeProfiles(config));

        using var document = JsonDocument.Parse(config.ToFileJson());
        var root = document.RootElement;
        var provider = root.GetProperty("Providers").GetProperty("default");
        var profile = root.GetProperty("Profiles").GetProperty("default");

        Assert.Equal("default", root.GetProperty("CurrentProfile").GetString());
        Assert.Equal(config.BaseUrl, provider.GetProperty("BaseUrl").GetString());
        Assert.Equal(JsonValueKind.Null, provider.GetProperty("ApiKey").ValueKind);
        Assert.Equal(JsonValueKind.Null, provider.GetProperty("TimeoutSeconds").ValueKind);
        Assert.Equal(config.Model, profile.GetProperty("Model").GetString());
        Assert.Equal(JsonValueKind.Null, profile.GetProperty("Temperature").ValueKind);
        Assert.Equal(JsonValueKind.Null, profile.GetProperty("MaxTokens").ValueKind);
        Assert.Equal(JsonValueKind.Null, profile.GetProperty("CompactionThreshold").ValueKind);
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
    public void AgentConfigSwitchProfileClearsApiKeyForKeylessProvider()
    {
        var config = new AgentConfig
        {
            ApiKey = "previous-provider-key",
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

        Assert.Null(config.ApiKey);
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
