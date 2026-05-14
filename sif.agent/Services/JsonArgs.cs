using System.Text.Json;

namespace sif.agent;

internal static class JsonArgs
{
    public static string String(JsonElement root, string defaultValue, params string[] names)
    {
        return TryString(root, out var value, names) ? value : defaultValue;
    }

    public static bool TryString(JsonElement root, out string value, params string[] names)
    {
        foreach (var name in names)
        {
            if (!root.TryGetProperty(name, out var element))
                continue;

            value = element.ValueKind switch
            {
                JsonValueKind.String => element.GetString() ?? "",
                JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => element.ToString(),
                _ => ""
            };

            if (value.Length > 0)
                return true;
        }

        value = "";
        return false;
    }

    public static int Int(JsonElement root, int defaultValue, params string[] names)
    {
        foreach (var name in names)
        {
            if (!root.TryGetProperty(name, out var element))
                continue;

            if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var number))
                return number;

            if (element.ValueKind == JsonValueKind.String && int.TryParse(element.GetString(), out var parsed))
                return parsed;
        }

        return defaultValue;
    }

    public static double Double(JsonElement root, double defaultValue, params string[] names)
    {
        foreach (var name in names)
        {
            if (!root.TryGetProperty(name, out var element))
                continue;

            if (element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out var number))
                return number;

            if (element.ValueKind == JsonValueKind.String && double.TryParse(element.GetString(), out var parsed))
                return parsed;
        }

        return defaultValue;
    }

    public static string[] StringArray(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (!root.TryGetProperty(name, out var element))
                continue;

            if (element.ValueKind == JsonValueKind.String)
            {
                var value = element.GetString();
                return string.IsNullOrWhiteSpace(value) ? [] : [value];
            }

            if (element.ValueKind == JsonValueKind.Array)
            {
                return element.EnumerateArray()
                    .Select(item => item.ValueKind == JsonValueKind.String ? item.GetString() : item.ToString())
                    .Where(item => !string.IsNullOrWhiteSpace(item))
                    .Select(item => item!)
                    .ToArray();
            }
        }

        return [];
    }
}
