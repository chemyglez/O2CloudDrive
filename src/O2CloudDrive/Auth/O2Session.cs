using System.Text.Json.Serialization;

namespace O2CloudDrive.Auth;

public sealed record O2Session
{
    public string ValidationKey { get; init; } = string.Empty;
    public Dictionary<string, string> Cookies { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public string UserAgent { get; init; } = string.Empty;

    [JsonIgnore]
    public bool IsAuthenticated => !string.IsNullOrWhiteSpace(ValidationKey);

    [JsonIgnore]
    public string CookieHeader => string.Join("; ", Cookies
        .Where(pair => !string.IsNullOrWhiteSpace(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value))
        .Select(pair => $"{pair.Key}={pair.Value}"));
}
