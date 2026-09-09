using System.Net.Http.Headers;
using System.Text.Json;

namespace sif.agent.Services;

internal sealed record AvailableModel(string Id, bool? IsLoaded = null);

/// <summary>Best-effort discovery. Missing load metadata means unknown, never unloaded.</summary>
internal static class ModelCatalogService
{
    internal static async Task<IReadOnlyList<AvailableModel>> FetchAsync(string baseUrl, string? apiKey)
    {
        using var http = new HttpClient();
        if (!string.IsNullOrWhiteSpace(apiKey))
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        return await FetchAsync(baseUrl, http, TimeSpan.FromSeconds(4));
    }

    internal static async Task<IReadOnlyList<AvailableModel>> FetchAsync(
        string baseUrl, HttpClient http, TimeSpan timeout)
    {
        using var deadline = new CancellationTokenSource(timeout);
        // Probe independently so an unsupported, slow route cannot hide another route's results.
        var endpoints = GetModelEndpointCandidates(baseUrl).ToList();
        var responses = await Task.WhenAll(endpoints.Select(url => ReadAsync(http, url, deadline.Token)));
        var models = new Dictionary<string, AvailableModel>(StringComparer.Ordinal);
        for (var i = 0; i < responses.Length; i++)
        {
            if (responses[i] is not { } root)
                continue;
            foreach (var model in ParseModels(root))
            {
                if (!models.ContainsKey(model.Id) || model.IsLoaded.HasValue)
                    models[model.Id] = model;
            }

            // Ollama's catalog does not report load state; only a successful /ps does.
            if (endpoints[i].AbsolutePath.EndsWith("/api/tags", StringComparison.Ordinal) &&
                HasModelArray(root, "models"))
            {
                var runningRoot = await ReadAsync(http, new Uri(endpoints[i], "ps"), deadline.Token);
                if (runningRoot is { } running && HasModelArray(running, "models"))
                {
                    var loadedIds = ParseModels(running).Select(model => model.Id).ToHashSet(StringComparer.Ordinal);
                    foreach (var model in ParseModels(root))
                        models[model.Id] = model with { IsLoaded = loadedIds.Contains(model.Id) };
                }
            }
        }
        return models.Values.OrderBy(model => model.IsLoaded == false ? 1 : 0)
            .ThenBy(model => model.Id, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static async Task<JsonElement?> ReadAsync(HttpClient http, Uri url, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
                return null;
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            return document.RootElement.Clone();
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or OperationCanceledException)
        {
            return null;
        }
    }

    internal static IReadOnlyList<AvailableModel> ParseModels(JsonElement root)
    {
        var models = new Dictionary<string, AvailableModel>(StringComparer.Ordinal);
        var property = HasModelArray(root, "data") ? "data" : "models";
        if (!HasModelArray(root, property))
            return [];

        foreach (var item in root.GetProperty(property).EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;
            var id = ReadString(item, "id") ?? ReadString(item, "key") ??
                     ReadString(item, "model") ?? ReadString(item, "name");
            if (string.IsNullOrWhiteSpace(id))
                continue;

            bool? loaded = ReadString(item, "state")?.ToLowerInvariant() switch
            {
                "loaded" => true,
                "not-loaded" or "unloaded" => false,
                _ => null
            };
            if (item.TryGetProperty("loaded_instances", out var instances) && instances.ValueKind == JsonValueKind.Array)
            {
                loaded = instances.GetArrayLength() > 0;
                // Custom instance identifiers can also appear in the compatible model list.
                foreach (var instance in instances.EnumerateArray())
                {
                    if (instance.ValueKind == JsonValueKind.Object && ReadString(instance, "id") is { Length: > 0 } alias)
                        models[alias] = new AvailableModel(alias, true);
                }
            }
            if (!models.ContainsKey(id) || loaded.HasValue)
                models[id] = new AvailableModel(id, loaded);
        }
        return models.Values.ToList();
    }

    private static bool HasModelArray(JsonElement root, string property) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(property, out var models) &&
        models.ValueKind == JsonValueKind.Array;

    private static string? ReadString(JsonElement item, string property) =>
        item.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    internal static IEnumerable<Uri> GetModelEndpointCandidates(string baseUrl)
    {
        if (!Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            yield break;

        var path = uri.AbsolutePath.TrimEnd('/');
        var paths = new List<string> { $"{path}/models" };
        if (!path.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
            paths.Add($"{path}/v1/models");

        // Preserve reverse-proxy prefixes and the configured host/port.
        var prefix = path.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) ? path[..^3] : path;
        if (prefix.EndsWith("/api", StringComparison.OrdinalIgnoreCase))
            prefix = prefix[..^4];
        paths.Add($"{prefix}/api/v0/models");
        paths.Add($"{prefix}/api/v1/models");
        paths.Add($"{prefix}/api/tags");
        foreach (var candidate in paths.Distinct(StringComparer.Ordinal))
            yield return new UriBuilder(uri) { Path = candidate, Query = "", Fragment = "" }.Uri;
    }
}
