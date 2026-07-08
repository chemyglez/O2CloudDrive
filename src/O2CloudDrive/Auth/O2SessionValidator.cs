using System.Text.Json;

namespace O2CloudDrive.Auth;

public sealed class O2SessionValidator
{
    private static readonly ValidationProbe[] Probes =
    [
        new(HttpMethod.Get, "profile/role", "action=get"),
        new(HttpMethod.Get, "profile", "action=get"),
        new(HttpMethod.Get, "user/status", "action=get"),
        new(HttpMethod.Post, "media/folder/root", "action=get"),
    ];

    private readonly HttpClient _httpClient;
    private readonly Uri _baseUri;

    public O2SessionValidator(HttpClient httpClient, string apiBaseUrl)
    {
        _httpClient = httpClient;
        _baseUri = new Uri(apiBaseUrl, UriKind.Absolute);
    }

    public bool Validate(O2Session session)
    {
        if (!session.IsAuthenticated)
        {
            return false;
        }

        foreach (var probe in Probes)
        {
            try
            {
                using var request = new HttpRequestMessage(probe.Method, BuildUri(probe.Resource, probe.Query, session.ValidationKey));
                ApplySessionHeaders(request, session);
                using var response = _httpClient.Send(request, HttpCompletionOption.ResponseHeadersRead);
                if (IsAuthenticatedResponse(response))
                {
                    return true;
                }
            }
            catch
            {
            }
        }

        return false;
    }

    private Uri BuildUri(string resource, string query, string validationKey)
    {
        var builder = new UriBuilder(new Uri(_baseUri, resource.TrimStart('/')))
        {
            Query = $"{query}&validationkey={Uri.EscapeDataString(validationKey)}",
        };
        return builder.Uri;
    }

    private static bool IsAuthenticatedResponse(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
        {
            return false;
        }

        var text = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        var lower = text.ToLowerInvariant();
        if (lower.Contains("<html", StringComparison.Ordinal) && lower.Contains("login", StringComparison.Ordinal))
        {
            return false;
        }

        if (LooksLikeRejectedSession(lower))
        {
            return false;
        }

        if (TryParseRejectedJson(text, out var rejected))
        {
            return !rejected;
        }

        return !lower.Contains("\"error\"", StringComparison.Ordinal);
    }

    private static bool LooksLikeRejectedSession(string lower)
    {
        return lower.Contains("invalid", StringComparison.Ordinal) ||
               lower.Contains("unauthorized", StringComparison.Ordinal) ||
               lower.Contains("forbidden", StringComparison.Ordinal) ||
               lower.Contains("login", StringComparison.Ordinal) ||
               lower.Contains("session", StringComparison.Ordinal) && lower.Contains("expired", StringComparison.Ordinal);
    }

    private static bool TryParseRejectedJson(string text, out bool rejected)
    {
        rejected = false;
        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return true;
            }

            if (root.TryGetProperty("error", out var error) && IsMeaningfulError(error))
            {
                rejected = true;
                return true;
            }

            var success = FirstString(root, "success");
            if (success is not null && success.Equals("false", StringComparison.OrdinalIgnoreCase))
            {
                rejected = true;
                return true;
            }

            var status = FirstString(root, "status");
            if (status is not null &&
                (status.Contains("error", StringComparison.OrdinalIgnoreCase) ||
                 status.Contains("fail", StringComparison.OrdinalIgnoreCase)))
            {
                rejected = true;
                return true;
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsMeaningfulError(JsonElement error)
    {
        return error.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.Undefined or JsonValueKind.False => false,
            JsonValueKind.String => !string.IsNullOrWhiteSpace(error.GetString()) &&
                                    !IsSuccessValue(error.GetString()!),
            JsonValueKind.Number => error.GetRawText() != "0",
            JsonValueKind.Object => IsMeaningfulErrorObject(error),
            JsonValueKind.Array => error.GetArrayLength() > 0,
            _ => true,
        };
    }

    private static bool IsMeaningfulErrorObject(JsonElement error)
    {
        var code = FirstString(error, "code", "errorcode", "resultcode", "statuscode");
        if (!string.IsNullOrWhiteSpace(code) && IsSuccessValue(code))
        {
            return false;
        }

        var message = FirstString(error, "message", "description", "detail");
        if (!string.IsNullOrWhiteSpace(message) && IsSuccessValue(message))
        {
            return false;
        }

        return error.EnumerateObject().Any();
    }

    private static string? FirstString(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var property))
            {
                continue;
            }

            if (property.ValueKind == JsonValueKind.String)
            {
                return property.GetString();
            }

            if (property.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
            {
                return property.GetRawText();
            }
        }

        return null;
    }

    private static bool IsSuccessValue(string value)
    {
        var normalized = value.Trim().ToUpperInvariant();
        return normalized is "0" or "OK" or "SUCCESS" or "TRUE" or "COM-0000" or "COM-0";
    }

    private static void ApplySessionHeaders(HttpRequestMessage request, O2Session session)
    {
        if (!string.IsNullOrWhiteSpace(session.CookieHeader))
        {
            request.Headers.TryAddWithoutValidation("Cookie", session.CookieHeader);
        }

        if (!string.IsNullOrWhiteSpace(session.UserAgent))
        {
            request.Headers.TryAddWithoutValidation("User-Agent", session.UserAgent);
        }
    }

    private sealed record ValidationProbe(HttpMethod Method, string Resource, string Query);
}
