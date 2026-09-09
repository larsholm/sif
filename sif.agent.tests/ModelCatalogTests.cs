using System.Diagnostics;
using System.Net;
using System.Text.Json;
using sif.agent.Services;
using Spectre.Console;
using Xunit;

namespace sif.agent.tests;

public sealed class ModelCatalogTests
{
    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"data\":null}")]
    [InlineData("{\"error\":\"unsupported\"}")]
    public void UnrecognizedResponsesHaveNoModels(string json)
    {
        using var doc = JsonDocument.Parse(json);
        Assert.Empty(ModelCatalogService.ParseModels(doc.RootElement));
    }

    [Fact]
    public void CompatibleListsIgnoreMalformedEntriesAndPreserveUnknownLoadState()
    {
        using var doc = JsonDocument.Parse("""
            {"data":[null, 42, {}, {"id":3}, {"id":" "}, {"id":"remote"},
                     {"id":"remote"}, {"id":"Remote"}]}
            """);
        var models = ModelCatalogService.ParseModels(doc.RootElement);
        Assert.Equal(2, models.Count);
        Assert.All(models, model => Assert.Null(model.IsLoaded));
    }

    [Fact]
    public void NativeListsRecognizeLoadedInstancesAndCustomIds()
    {
        using var doc = JsonDocument.Parse("""
            {"models":[
                {"key":"warm","loaded_instances":[{"id":"custom-id"}]},
                {"key":"cold","loaded_instances":[]},
                {"key":"unknown"}]}
            """);
        var models = ModelCatalogService.ParseModels(doc.RootElement).ToDictionary(model => model.Id);
        Assert.True(models["warm"].IsLoaded);
        Assert.True(models["custom-id"].IsLoaded);
        Assert.False(models["cold"].IsLoaded);
        Assert.Null(models["unknown"].IsLoaded);
    }

    [Fact]
    public async Task NativeMetadataEnrichesCompatibleListAndIncludesUnloadedModels()
    {
        using var http = CreateClient(path => path switch
        {
            "/v1/models" => Json("""{"data":[{"id":"warm"},{"id":"remote"}]}"""),
            "/api/v0/models" => Json("""{"data":[{"id":"warm","state":"not-loaded"}]}"""),
            "/api/v1/models" => Json("""
                {"models":[{"key":"warm","loaded_instances":[{"id":"warm"}]},
                           {"key":"cold","loaded_instances":[]}]}
                """),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        });
        var models = await ModelCatalogService.FetchAsync("http://provider/v1", http, TimeSpan.FromSeconds(1));
        Assert.Equal(3, models.Count);
        Assert.True(models.Single(model => model.Id == "warm").IsLoaded);
        Assert.Null(models.Single(model => model.Id == "remote").IsLoaded);
        Assert.False(models.Single(model => model.Id == "cold").IsLoaded);
    }

    [Fact]
    public async Task LegacyLoadStatesAreSupported()
    {
        using var http = CreateClient(path => path == "/api/v0/models"
            ? Json("""{"data":[{"id":"warm","state":"loaded"},{"id":"cold","state":"not-loaded"}]}""")
            : new HttpResponseMessage(HttpStatusCode.NotFound));
        var models = await ModelCatalogService.FetchAsync("http://provider/v1", http, TimeSpan.FromSeconds(1));
        Assert.True(models.Single(model => model.Id == "warm").IsLoaded);
        Assert.False(models.Single(model => model.Id == "cold").IsLoaded);
    }

    [Theory]
    [InlineData("{\"models\":[{\"model\":\"warm:latest\"}]}", true, false)]
    [InlineData("{\"models\":[]}", false, false)]
    [InlineData("{\"error\":\"unsupported\"}", null, null)]
    [InlineData("invalid json", null, null)]
    [InlineData(null, null, null)]
    public async Task OllamaOnlyMarksModelsUnloadedAfterSuccessfulRunningList(
        string? runningJson, bool? warmLoaded, bool? coldLoaded)
    {
        using var http = CreateClient(path => path switch
        {
            "/api/tags" => Json("""{"models":[{"name":"warm:latest"},{"model":"cold:latest"}]}"""),
            "/api/ps" when runningJson is not null => Json(runningJson),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        });
        var models = await ModelCatalogService.FetchAsync("http://provider/v1", http, TimeSpan.FromSeconds(1));
        Assert.Equal(warmLoaded, models.Single(model => model.Id == "warm:latest").IsLoaded);
        Assert.Equal(coldLoaded, models.Single(model => model.Id == "cold:latest").IsLoaded);
    }

    [Fact]
    public async Task OneDeadlineBoundsAllProbesAndPreservesSuccessfulResults()
    {
        using var http = new HttpClient(new StubHandler(async (request, ct) =>
        {
            if (request.RequestUri!.AbsolutePath == "/v1/models")
                return Json("""{"data":[{"id":"available"}]}""");
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            throw new InvalidOperationException("Expected cancellation");
        }));
        var stopwatch = Stopwatch.StartNew();
        var models = await ModelCatalogService.FetchAsync("http://provider/v1", http, TimeSpan.FromMilliseconds(50));
        Assert.Equal("available", Assert.Single(models).Id);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task FailingEndpointsLeaveManualEntryAvailable()
    {
        using var http = new HttpClient(new StubHandler((_, _) => throw new HttpRequestException("Offline")));
        Assert.Empty(await ModelCatalogService.FetchAsync("http://provider/v1", http, TimeSpan.FromSeconds(1)));
        Assert.Empty(await ModelCatalogService.FetchAsync("not a URL", http, TimeSpan.FromSeconds(1)));
    }

    [Theory]
    [InlineData("https://provider:8443/proxy/v1/", "/proxy")]
    [InlineData("https://provider:8443/proxy", "/proxy")]
    [InlineData("https://provider:8443/v1", "")]
    public void NativeProbesPreserveProviderOriginAndProxyPrefix(string baseUrl, string prefix)
    {
        var endpoints = ModelCatalogService.GetModelEndpointCandidates(baseUrl).ToList();
        Assert.All(endpoints, endpoint => Assert.Equal("https://provider:8443", endpoint.GetLeftPart(UriPartial.Authority)));
        Assert.Contains(endpoints, endpoint => endpoint.AbsolutePath == $"{prefix}/api/v1/models");
        Assert.Contains(endpoints, endpoint => endpoint.AbsolutePath == $"{prefix}/api/tags");
        Assert.Equal(endpoints.Count, endpoints.Distinct().Count());
    }

    [Fact]
    public void PickerOnlyShowsSelectedProvidersSavedAndDiscoveredModels()
    {
        var config = new AgentConfig
        {
            CurrentProfile = "favorite",
            Profiles = new()
            {
                ["favorite"] = new() { Name = "favorite", Provider = "local", Model = "cold" },
                ["offline"] = new() { Name = "offline", Provider = "offline", Model = "missing" }
            }
        };
        var catalogs = new Dictionary<string, IReadOnlyList<AvailableModel>>
        {
            ["local"] = [new("cold", false), new("warm", true)],
            ["remote"] = [new("cold")]
        };
        var choices = ModelSelection.BuildChoices(config, catalogs, "local");
        Assert.Equal(2, choices.Count);
        Assert.All(choices, choice => Assert.Equal("local", choice.ProviderName));
        Assert.Equal("favorite", choices[0].ProfileName);
        Assert.False(choices[0].IsLoaded);
        Assert.Null(Assert.Single(ModelSelection.BuildChoices(config, catalogs, "offline")).IsLoaded);
        Assert.Null(Assert.Single(ModelSelection.BuildChoices(config, catalogs, "remote")).IsLoaded);
        Assert.Equal(2, config.Profiles.Count);
    }

    [Fact]
    public void ProviderPickerPrioritizesCurrentProviderAndKeepsOrphanedProfilesAccessible()
    {
        var config = new AgentConfig
        {
            CurrentProfile = "favorite",
            Providers = new()
            {
                ["commercial"] = new() { BaseUrl = "https://example.com/v1" },
                ["local"] = new() { BaseUrl = "http://localhost:1234/v1" }
            },
            Profiles = new()
            {
                ["favorite"] = new() { Provider = "local", Model = "warm" },
                ["old"] = new() { Provider = "missing", Model = "old" },
                ["legacy"] = new() { Model = "legacy" }
            }
        };
        var providers = ModelSelection.BuildProviderChoices(config);
        Assert.Equal(4, providers.Count);
        Assert.Equal("local", providers[0].Name);
        Assert.Contains("localhost:1234", providers[0].Label);
        Assert.Contains(providers, provider => provider.Name == "commercial");
        Assert.Contains(providers, provider => provider.Name == "missing");
        Assert.Contains(providers, provider => provider.Name is null);
        var legacy = ModelSelection.BuildChoices(config, new Dictionary<string, IReadOnlyList<AvailableModel>>(), null);
        Assert.Equal("legacy", Assert.Single(legacy).ProfileName);
    }

    [Fact]
    public void SelectingDiscoveredModelCreatesReusableProfileWithoutOverwritingPresets()
    {
        var config = new AgentConfig
        {
            Providers = new() { ["local"] = new() { Name = "local", BaseUrl = "http://provider/v1", ApiKey = "key" } },
            Profiles = new() { ["local/new-model"] = new() { Model = "old-model", Provider = "local" } }
        };
        var choice = new ModelSelection(null, "local", "new-model", false);
        var name = choice.GetOrCreateProfile(config);
        Assert.Equal("local/new-model-2", name);
        Assert.Equal(name, choice.GetOrCreateProfile(config));
        Assert.Equal("old-model", config.Profiles["local/new-model"].Model);
        Assert.True(config.SwitchProfile(name));
        Assert.Equal("new-model", config.Model);
        Assert.Equal("http://provider/v1", config.BaseUrl);
        Assert.Equal("key", config.ApiKey);
        using var saved = JsonDocument.Parse(config.ToFileJson());
        Assert.Equal(name, saved.RootElement.GetProperty("CurrentProfile").GetString());
        Assert.Equal("local", saved.RootElement.GetProperty("Profiles").GetProperty(name).GetProperty("Provider").GetString());
    }

    [Fact]
    public void UnloadedLabelsAreGreyAndEscapeProviderSuppliedMarkup()
    {
        var choice = new ModelSelection("[profile]", "[provider]", "model[1]", false);
        Assert.StartsWith("[grey]", choice.Label);
        Assert.Contains("(unloaded)", choice.Label);
        Assert.Equal("[profile] — model[1] @[provider] (unloaded)", Markup.Remove(choice.Label));
        Assert.DoesNotContain("unloaded", (choice with { IsLoaded = null }).Label);
    }

    private static HttpClient CreateClient(Func<string, HttpResponseMessage> respond) =>
        new(new StubHandler((request, _) => Task.FromResult(respond(request.RequestUri!.AbsolutePath))));

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json) };

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handle) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            handle(request, cancellationToken);
    }
}
