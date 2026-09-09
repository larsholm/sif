using Spectre.Console;

namespace sif.agent.Services;

internal sealed record ModelProviderSelection(string? Name, string Label);

internal sealed record ModelSelection(string? ProfileName, string? ProviderName, string Model, bool? IsLoaded)
{
    internal static IReadOnlyList<ModelProviderSelection> BuildProviderChoices(AgentConfig config)
    {
        var currentProvider = config.Profiles.GetValueOrDefault(config.CurrentProfile ?? "default")?.Provider;
        return config.Providers.Keys.Cast<string?>()
            .Concat(config.Profiles.Values.Select(profile => profile.Provider))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name == currentProvider ? 0 : 1)
            .ThenBy(name => name, StringComparer.OrdinalIgnoreCase)
            .Select(name => new ModelProviderSelection(name,
                name is null ? "Other saved profiles" :
                config.Providers.TryGetValue(name, out var provider)
                    ? $"{name.EscapeMarkup()} [dim]({provider.BaseUrl.EscapeMarkup()})[/]"
                    : name.EscapeMarkup()))
            .ToList();
    }

    internal string Label
    {
        get
        {
            var label = ProfileName is null ? Model.EscapeMarkup() : $"{ProfileName.EscapeMarkup()} — {Model.EscapeMarkup()}";
            if (ProviderName is not null)
                label += $" @{ProviderName.EscapeMarkup()}";
            return FormatLabel(label, IsLoaded);
        }
    }

    internal static string FormatLabel(string escapedLabel, bool? isLoaded) => isLoaded switch
    {
        false => $"[grey]{escapedLabel} (unloaded)[/]",
        true => $"{escapedLabel} [dim](loaded)[/]",
        null => escapedLabel
    };

    internal static IReadOnlyList<ModelSelection> BuildChoices(
        AgentConfig config, IReadOnlyDictionary<string, IReadOnlyList<AvailableModel>> catalogs, string? providerName)
    {
        var choices = new List<ModelSelection>();
        foreach (var (name, profile) in config.Profiles)
        {
            if (profile.Provider != providerName)
                continue;
            bool? loaded = null;
            if (profile.Provider is not null && catalogs.TryGetValue(profile.Provider, out var models))
                loaded = models.FirstOrDefault(model => model.Id == profile.Model)?.IsLoaded;
            choices.Add(new ModelSelection(name, profile.Provider, profile.Model, loaded));
        }

        foreach (var (provider, models) in catalogs)
        {
            if (provider != providerName)
                continue;
            foreach (var model in models)
            {
                if (!choices.Any(choice => choice.ProviderName == provider && choice.Model == model.Id))
                    choices.Add(new ModelSelection(null, provider, model.Id, model.IsLoaded));
            }
        }

        return choices.OrderBy(choice => choice.ProfileName == (config.CurrentProfile ?? "default") ? 0 : 1)
            .ThenBy(choice => choice.IsLoaded == false ? 1 : 0)
            .ThenBy(choice => choice.ProfileName ?? choice.Model, StringComparer.OrdinalIgnoreCase)
            .ThenBy(choice => choice.ProviderName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    // Only the selected model becomes a profile. Never overwrite an existing preset.
    internal string GetOrCreateProfile(AgentConfig config)
    {
        if (ProfileName is not null)
            return ProfileName;
        var existing = config.Profiles.FirstOrDefault(pair => pair.Value.Provider == ProviderName && pair.Value.Model == Model);
        if (existing.Key is not null)
            return existing.Key;

        var baseName = $"{ProviderName}/{Model}";
        var name = baseName;
        for (var suffix = 2; config.Profiles.ContainsKey(name); suffix++)
            name = $"{baseName}-{suffix}";
        config.Profiles[name] = new ModelProfile { Name = name, Provider = ProviderName, Model = Model };
        return name;
    }
}
