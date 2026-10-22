namespace O2CloudDrive.Auth;

internal static class O2SessionRequest
{
    private static string DefaultUserAgent => $"O2CloudDrive/{AppVersion.PackageVersion}";

    public static HttpRequestMessage Create(
        HttpMethod method,
        Uri baseUri,
        string resource,
        IReadOnlyDictionary<string, string?> query,
        O2Session session)
    {
        var request = new HttpRequestMessage(method, BuildUri(baseUri, resource, query, session));
        ApplyHeaders(request, baseUri, session);
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        return request;
    }

    public static Uri BuildUri(
        Uri baseUri,
        string resource,
        IReadOnlyDictionary<string, string?> query,
        O2Session session)
    {
        var builder = new UriBuilder(new Uri(baseUri, resource.TrimStart('/')));
        var parameters = query
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Value))
            .Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value!)}")
            .ToList();

        if (session.IsAuthenticated)
        {
            parameters.Add($"validationkey={Uri.EscapeDataString(session.ValidationKey)}");
        }

        builder.Query = string.Join("&", parameters);
        return builder.Uri;
    }

    public static void ApplyHeaders(
        HttpRequestMessage request,
        Uri baseUri,
        O2Session session,
        bool includeCookies = true)
    {
        if (includeCookies && !string.IsNullOrWhiteSpace(session.CookieHeader))
        {
            request.Headers.TryAddWithoutValidation("Cookie", session.CookieHeader);
        }

        request.Headers.TryAddWithoutValidation(
            "User-Agent",
            string.IsNullOrWhiteSpace(session.UserAgent) ? DefaultUserAgent : session.UserAgent);
        request.Headers.TryAddWithoutValidation("Origin", baseUri.GetLeftPart(UriPartial.Authority));
        request.Headers.TryAddWithoutValidation("Referer", baseUri.GetLeftPart(UriPartial.Authority) + "/");
        request.Headers.TryAddWithoutValidation("Accept-Language", "es-ES,es;q=0.9,en;q=0.8");
    }

    public static bool IsSameOrigin(Uri left, Uri right)
    {
        return left.Scheme.Equals(right.Scheme, StringComparison.OrdinalIgnoreCase) &&
               left.IdnHost.Equals(right.IdnHost, StringComparison.OrdinalIgnoreCase) &&
               left.Port == right.Port;
    }
}
