using sif.agent.Services;
using Xunit;
using Xunit.Abstractions;

namespace sif.agent.tests;

public sealed class DefaultProfileLiveIntegrationTests
{
    private readonly ITestOutputHelper _output;

    public DefaultProfileLiveIntegrationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task CompleteAsyncCanUseDefaultProfileForLiveLocalModel()
    {
        if (!LiveModelTestsEnabled())
        {
            _output.WriteLine("Skipped. Set SIF_RUN_LIVE_MODEL_TESTS=1 to call the default model profile.");
            return;
        }

        var config = LoadDefaultProfileConfig();
        Assert.False(string.IsNullOrWhiteSpace(config.BaseUrl), "The default profile must define BaseUrl.");
        Assert.False(string.IsNullOrWhiteSpace(config.Model), "The default profile must define Model.");

        _output.WriteLine($"Calling {config.Model} @ {config.BaseUrl}");

        var client = new AgentClient(config);
        var (response, _) = await WithTimeout(client.CompleteAsync(
            "Reply with exactly: pong",
            "You are a health-check endpoint. Return only the requested literal text."));

        Assert.Contains("pong", response, StringComparison.OrdinalIgnoreCase);
    }

    private static bool LiveModelTestsEnabled()
    {
        var value = Environment.GetEnvironmentVariable("SIF_RUN_LIVE_MODEL_TESTS");
        return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
    }

    private static AgentConfig LoadDefaultProfileConfig()
    {
        var loaded = AgentConfig.Load();
        if (!loaded.Profiles.TryGetValue("default", out var profile))
            throw new InvalidOperationException("No default model profile is configured.");

        var config = profile.ToConfig();
        config.CurrentProfile = "default";
        config.Profiles = loaded.Profiles;
        config.Tools = loaded.Tools;
        config.ShellAllowedCommands = loaded.ShellAllowedCommands;
        config.McpServers = loaded.McpServers;
        config.Values = loaded.Values;

        if (profile.UseSecureApiKeyStorage && string.IsNullOrEmpty(config.ApiKey))
        {
            var credentialStore = SecureCredentialStoreFactory.Create();
            var secureKey = credentialStore.RetrieveAsync("api-key-default").GetAwaiter().GetResult();
            if (!string.IsNullOrEmpty(secureKey))
                config.ApiKey = secureKey;
        }

        return config;
    }

    private static async Task<T> WithTimeout<T>(Task<T> task)
    {
        var completed = await Task.WhenAny(task, Task.Delay(TimeSpan.FromMinutes(2)));
        if (completed != task)
            throw new TimeoutException("Default profile live model call did not complete.");

        return await task;
    }
}
