namespace O2CloudDrive.Auth;

public sealed class O2SessionValidator
{
    private static readonly ValidationProbe[] Probes =
    [
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

        return !lower.Contains("\"error\"", StringComparison.Ordinal) ||
               (!lower.Contains("invalid", StringComparison.Ordinal) &&
                !lower.Contains("unauthorized", StringComparison.Ordinal) &&
                !lower.Contains("forbidden", StringComparison.Ordinal));
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
