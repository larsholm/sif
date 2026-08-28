using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace sif.agent.Services;

internal enum ModelReadinessResult
{
    AlreadyLoaded,
    Loaded,
    Unavailable,
}

/// <summary>
/// Uses LM Studio's model-management API when available to make sure a model
/// runtime that crashed or was unloaded is ready before goal evaluation resumes.
/// Other OpenAI-compatible providers are left untouched.
/// </summary>
internal sealed class ModelReadinessService
{
    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(3);
    private const int DefaultMaximumPolls = 20;

    private readonly string _model;
    private readonly IReadOnlyList<Uri> _managementEndpoints;
    private readonly HttpClient _http;
    private readonly TimeSpan _pollInterval;
    private readonly int _maximumPolls;

    internal ModelReadinessService(AgentConfig config)
        : this(config, new HttpClient { Timeout = TimeSpan.FromMinutes(2) }, DefaultPollInterval, DefaultMaximumPolls)
    {
    }

    internal ModelReadinessService(
        AgentConfig config,
        HttpClient http,
        TimeSpan pollInterval,
        int maximumPolls)
    {
        _model = config.Model;
        _managementEndpoints = BuildManagementEndpoints(config.BaseUrl);
        _http = http;
        _pollInterval = pollInterval;
        _maximumPolls = Math.Max(1, maximumPolls);

        if (!string.IsNullOrWhiteSpace(config.ApiKey))
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", config.ApiKey);
    }

    internal async Task<ModelReadinessResult> EnsureLoadedAsync(CancellationToken cancellationToken = default)
    {
        foreach (var endpoint in _managementEndpoints)
        {
            var state = await TryGetStateAsync(endpoint, cancellationToken);
            if (state == ModelState.Unknown)
                continue;
            if (state == ModelState.Loaded)
                return ModelReadinessResult.AlreadyLoaded;
            if (state == ModelState.NotFound)
                continue;

            var loadRequested = await TryRequestLoadAsync(endpoint, cancellationToken);
            if (!loadRequested)
                continue;

            for (var poll = 0; poll < _maximumPolls; poll++)
            {
                var loadedState = await TryGetStateAsync(endpoint, cancellationToken);
                if (loadedState == ModelState.Loaded)
                    return ModelReadinessResult.Loaded;

                if (poll + 1 < _maximumPolls)
                    await Task.Delay(_pollInterval, cancellationToken);
            }
        }

        return ModelReadinessResult.Unavailable;
    }

    internal static IReadOnlyList<Uri> BuildManagementEndpoints(string baseUrl)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri) ||
            (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps))
        {
            return [];
        }

        var endpoints = new List<Uri>();
        AddEndpoint(new UriBuilder(baseUri) { Path = "/api/v1/models", Query = "" }.Uri);
        AddEndpoint(new UriBuilder(baseUri)
        {
            Port = 1234,
            Path = "/api/v1/models",
            Query = ""
        }.Uri);
        return endpoints;

        void AddEndpoint(Uri endpoint)
        {
            if (!endpoints.Contains(endpoint))
                endpoints.Add(endpoint);
        }
    }

    private async Task<ModelState> TryGetStateAsync(Uri endpoint, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _http.GetAsync(endpoint, cancellationToken);
            if (!response.IsSuccessStatusCode)
                return ModelState.Unknown;

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (!doc.RootElement.TryGetProperty("models", out var models) ||
                models.ValueKind != JsonValueKind.Array)
            {
                return ModelState.Unknown;
            }

            foreach (var model in models.EnumerateArray())
            {
                if (!model.TryGetProperty("key", out var key) ||
                    !string.Equals(key.GetString(), _model, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                return model.TryGetProperty("loaded_instances", out var instances) &&
                       instances.ValueKind == JsonValueKind.Array &&
                       instances.GetArrayLength() > 0
                    ? ModelState.Loaded
                    : ModelState.Unloaded;
            }

            return ModelState.NotFound;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or TaskCanceledException)
        {
            if (ex is OperationCanceledException && cancellationToken.IsCancellationRequested)
                throw;
            return ModelState.Unknown;
        }
    }

    private async Task<bool> TryRequestLoadAsync(Uri modelsEndpoint, CancellationToken cancellationToken)
    {
        try
        {
            var endpoint = new Uri(modelsEndpoint, "models/load");
            var json = JsonSerializer.Serialize(new { model = _model });
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var response = await _http.PostAsync(endpoint, content, cancellationToken);
            return response.IsSuccessStatusCode ||
                   response.StatusCode == System.Net.HttpStatusCode.Conflict;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
        {
            if (ex is OperationCanceledException && cancellationToken.IsCancellationRequested)
                throw;
            return false;
        }
    }

    private enum ModelState
    {
        Unknown,
        NotFound,
        Unloaded,
        Loaded,
    }
}
