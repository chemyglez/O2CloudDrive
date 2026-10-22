using System.Text.Json;
using System.Text.RegularExpressions;

namespace O2CloudDrive.Auth;

internal static class O2ValidationKey
{
    private static readonly Regex QueryRegex = new(
        "(?:^|[?&])validation_?key=([^&#\\s\\\"']+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex JsonRegex = new(
        "\\\"validation_?key\\\"\\s*:\\s*\\\"([^\\\"\\r\\n]{8,2048})\\\"",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static string? Normalize(string? value)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return null;
        }

        if (normalized.Length is < 8 or > 2048 ||
            normalized.Equals("undefined", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("null", StringComparison.OrdinalIgnoreCase) ||
            normalized.Any(char.IsWhiteSpace) ||
            normalized.Any(char.IsControl))
        {
            return null;
        }

        return normalized;
    }

    public static IReadOnlyList<string> ExtractFromText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var keys = new List<string>();
        foreach (Match match in QueryRegex.Matches(text))
        {
            var queryValue = match.Groups[1].Value;
            try
            {
                queryValue = Uri.UnescapeDataString(queryValue);
            }
            catch (UriFormatException)
            {
            }

            Add(keys, queryValue);
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            foreach (var key in ExtractFromJson(document.RootElement))
            {
                Add(keys, key);
            }
        }
        catch (JsonException)
        {
        }

        foreach (var candidateText in new[] { text, text.Replace("\\\"", "\"", StringComparison.Ordinal) })
        {
            foreach (Match match in JsonRegex.Matches(candidateText))
            {
                Add(keys, match.Groups[1].Value);
            }
        }

        return keys;
    }

    public static IReadOnlyList<string> ExtractFromUri(string? uriText)
    {
        if (!Uri.TryCreate(uriText, UriKind.Absolute, out var uri))
        {
            return [];
        }

        var keys = new List<string>();
        foreach (Match match in QueryRegex.Matches(uri.Query))
        {
            var queryValue = match.Groups[1].Value;
            try
            {
                queryValue = Uri.UnescapeDataString(queryValue);
            }
            catch (UriFormatException)
            {
            }

            Add(keys, queryValue);
        }

        return keys;
    }

    public static IReadOnlyList<string> ExtractFromJson(JsonElement root)
    {
        var keys = new List<string>();
        ScanJson(root, keys, 0);
        return keys;
    }

    public static string? ExtractFromCookies(IReadOnlyDictionary<string, string> cookies)
    {
        foreach (var pair in cookies)
        {
            if (IsValidationKeyName(pair.Key))
            {
                var normalized = Normalize(pair.Value);
                if (normalized is not null)
                {
                    return normalized;
                }
            }
        }

        return null;
    }

    public static bool IsValidationKeyName(string? name)
    {
        return name is not null &&
               (name.Equals("validationkey", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("validation_key", StringComparison.OrdinalIgnoreCase));
    }

    private static void ScanJson(JsonElement element, ICollection<string> keys, int depth)
    {
        if (depth > 16)
        {
            return;
        }

        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (IsValidationKeyName(property.Name) && property.Value.ValueKind == JsonValueKind.String)
                {
                    Add(keys, property.Value.GetString());
                }

                if (property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                {
                    ScanJson(property.Value, keys, depth + 1);
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in element.EnumerateArray())
            {
                if (child.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                {
                    ScanJson(child, keys, depth + 1);
                }
            }
        }
    }

    private static void Add(ICollection<string> keys, string? value)
    {
        var normalized = Normalize(value);
        if (normalized is not null && !keys.Contains(normalized, StringComparer.Ordinal))
        {
            keys.Add(normalized);
        }
    }
}
