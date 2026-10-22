using O2CloudDrive.Api;

namespace O2CloudDrive.Auth;

public sealed class O2SessionValidator
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);
    private readonly HttpClient _httpClient;
    private readonly Uri _baseUri;

    public O2SessionValidator(HttpClient httpClient, string apiBaseUrl)
    {
        _httpClient = httpClient;
        _baseUri = new Uri(apiBaseUrl, UriKind.Absolute);
    }

    public O2SessionValidationStatus Validate(O2Session session, CancellationToken cancellationToken = default)
    {
        return ValidateSession(session, cancellationToken).Status;
    }

    public O2SessionValidationResult ValidateSession(
        O2Session session,
        CancellationToken cancellationToken = default)
    {
        if (!session.IsAuthenticated)
        {
            return new O2SessionValidationResult(O2SessionValidationStatus.Rejected);
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(RequestTimeout);
            using var request = O2SessionRequest.Create(
                HttpMethod.Get,
                _baseUri,
                "media/folder/root",
                new Dictionary<string, string?> { ["action"] = "get" },
                session);
            using var response = _httpClient.Send(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                return new O2SessionValidationResult(O2SessionValidationStatus.Rejected);
            }

            if (!response.IsSuccessStatusCode)
            {
                return new O2SessionValidationResult(O2SessionValidationStatus.Unavailable);
            }

            var text = response.Content.ReadAsStringAsync(timeout.Token).GetAwaiter().GetResult();
            using var document = O2CloudApiClient.ParseJson(text);
            if (O2CloudApiClient.HasAuthenticationError(document.RootElement))
            {
                return new O2SessionValidationResult(O2SessionValidationStatus.Rejected);
            }

            O2CloudApiClient.ThrowIfO2Error(document.RootElement, text);
            if (!O2CloudApiClient.HasRootFolder(document.RootElement))
            {
                return new O2SessionValidationResult(O2SessionValidationStatus.Unavailable);
            }

            var refreshedKey = O2ValidationKey.ExtractFromJson(document.RootElement).FirstOrDefault();
            var validatedSession = refreshedKey is null ||
                                   refreshedKey.Equals(session.ValidationKey, StringComparison.Ordinal)
                ? session
                : session with { ValidationKey = refreshedKey };
            return new O2SessionValidationResult(O2SessionValidationStatus.Valid, validatedSession);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException or InvalidOperationException or System.Text.Json.JsonException)
        {
            return new O2SessionValidationResult(O2SessionValidationStatus.Unavailable);
        }
    }
}
