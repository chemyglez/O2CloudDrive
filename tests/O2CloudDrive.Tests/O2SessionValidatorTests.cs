using System.Net;
using O2CloudDrive.Auth;

namespace O2CloudDrive.Tests;

public sealed class O2SessionValidatorTests
{
    private static readonly O2Session TestSession = new()
    {
        ValidationKey = "current-key-123",
        Cookies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["session-cookie"] = "cookie-value",
        },
        UserAgent = "O2CloudDrive.Tests/1.0",
    };

    [Fact]
    public void Validate_RejectsProfileSuccessWhenRootIsUnauthorized()
    {
        using var handler = new RecordingHandler(request =>
            request.Uri.AbsolutePath.EndsWith("/profile/role", StringComparison.Ordinal)
                ? JsonResponse(HttpStatusCode.OK, "{\"role\":\"user\"}")
                : JsonResponse(HttpStatusCode.Unauthorized, "{\"error\":\"unauthorized\"}"));
        using var httpClient = new HttpClient(handler);
        var validator = new O2SessionValidator(httpClient, "https://cloud.o2online.es/sapi/");

        Assert.Equal(O2SessionValidationStatus.Rejected, validator.Validate(TestSession));

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.EndsWith("/sapi/media/folder/root", request.Uri.AbsolutePath, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_AcceptsRootAndUsesMountRequestContract()
    {
        using var handler = new RecordingHandler(_ => JsonResponse(
            HttpStatusCode.OK,
            "{\"data\":{\"folders\":[{\"folderid\":\"root-test\",\"name\":\"\"}]}}"));
        using var httpClient = new HttpClient(handler);
        var validator = new O2SessionValidator(httpClient, "https://cloud.o2online.es/sapi/");

        Assert.Equal(O2SessionValidationStatus.Valid, validator.Validate(TestSession));

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Contains("action=get", request.Uri.Query, StringComparison.Ordinal);
        Assert.Contains("validationkey=current-key-123", request.Uri.Query, StringComparison.Ordinal);
        Assert.Equal("session-cookie=cookie-value", request.Header("Cookie"));
        Assert.Equal("O2CloudDrive.Tests/1.0", request.Header("User-Agent"));
        Assert.Equal("application/json", request.Header("Accept"));
        Assert.Equal("https://cloud.o2online.es", request.Header("Origin"));
        Assert.Equal("https://cloud.o2online.es/", request.Header("Referer"));
        Assert.Equal(
            "es-ES,es;q=0.9,en;q=0.8",
            request.Header("Accept-Language").Replace(" ", string.Empty, StringComparison.Ordinal));
        Assert.Equal(string.Empty, request.Header("X-deviceid"));
    }

    [Fact]
    public void ValidateSession_PropagatesRotatedKeyFromSuccessfulRootResponse()
    {
        using var handler = new RecordingHandler(_ => JsonResponse(
            HttpStatusCode.OK,
            "{\"data\":{\"folder\":{\"folderid\":\"root-test\"},\"validation_key\":\"rotated-key-456\"}}"));
        using var httpClient = new HttpClient(handler);
        var validator = new O2SessionValidator(httpClient, "https://cloud.o2online.es/sapi/");

        var result = validator.ValidateSession(TestSession);

        Assert.Equal(O2SessionValidationStatus.Valid, result.Status);
        Assert.NotNull(result.ValidatedSession);
        Assert.Equal("rotated-key-456", result.ValidatedSession.ValidationKey);
        Assert.Same(TestSession.Cookies, result.ValidatedSession.Cookies);
        Assert.Equal(TestSession.UserAgent, result.ValidatedSession.UserAgent);
    }

    [Fact]
    public void ValidateSession_DoesNotPropagateKeyFromAuthenticationError()
    {
        using var handler = new RecordingHandler(_ => JsonResponse(
            HttpStatusCode.OK,
            "{\"error\":\"unauthorized\",\"validationKey\":\"untrusted-key-456\"}"));
        using var httpClient = new HttpClient(handler);
        var validator = new O2SessionValidator(httpClient, "https://cloud.o2online.es/sapi/");

        var result = validator.ValidateSession(TestSession);

        Assert.Equal(O2SessionValidationStatus.Rejected, result.Status);
        Assert.Null(result.ValidatedSession);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("<html><body>login</body></html>")]
    public void Validate_RejectsSuccessWithoutRootFolder(string responseBody)
    {
        using var handler = new RecordingHandler(_ => JsonResponse(HttpStatusCode.OK, responseBody));
        using var httpClient = new HttpClient(handler);
        var validator = new O2SessionValidator(httpClient, "https://cloud.o2online.es/sapi/");

        Assert.Equal(O2SessionValidationStatus.Unavailable, validator.Validate(TestSession));
    }

    [Theory]
    [InlineData("{\"authenticated\":false}")]
    [InlineData("{\"error\":\"unauthorized\"}")]
    [InlineData("{\"errorcode\":\"401\"}")]
    [InlineData("{\"error\":{\"code\":401,\"message\":\"rejected\"}}")]
    [InlineData("{\"data\":{\"error\":{\"code\":401,\"message\":\"unauthorized\"},\"folder\":[{\"folderid\":\"must-not-pass\"}]}}")]
    public void Validate_RejectsExplicitAuthenticationErrorInSuccessfulResponse(string responseBody)
    {
        using var handler = new RecordingHandler(_ => JsonResponse(HttpStatusCode.OK, responseBody));
        using var httpClient = new HttpClient(handler);
        var validator = new O2SessionValidator(httpClient, "https://cloud.o2online.es/sapi/");

        Assert.Equal(O2SessionValidationStatus.Rejected, validator.Validate(TestSession));
    }

    [Theory]
    [InlineData("{\"success\":false,\"folders\":[{\"folderid\":\"must-not-pass\"}]}")]
    [InlineData("{\"error\":{\"code\":500,\"message\":\"failed\"},\"children\":[{\"folderid\":\"must-not-pass\"}]}")]
    [InlineData("{\"data\":{\"error\":{\"code\":500,\"message\":\"failed\"},\"items\":[{\"folderid\":\"must-not-pass\"}]}}")]
    public void Validate_DoesNotAcceptRootShapeWhenPayloadReportsError(string responseBody)
    {
        using var handler = new RecordingHandler(_ => JsonResponse(HttpStatusCode.OK, responseBody));
        using var httpClient = new HttpClient(handler);
        var validator = new O2SessionValidator(httpClient, "https://cloud.o2online.es/sapi/");

        Assert.Equal(O2SessionValidationStatus.Unavailable, validator.Validate(TestSession));
    }

    [Fact]
    public void Validate_PreservesSessionWhenServerReturnsForbidden()
    {
        using var handler = new RecordingHandler(_ => JsonResponse(HttpStatusCode.Forbidden, "{}"));
        using var httpClient = new HttpClient(handler);
        var validator = new O2SessionValidator(httpClient, "https://cloud.o2online.es/sapi/");

        Assert.Equal(O2SessionValidationStatus.Unavailable, validator.Validate(TestSession));
    }

    [Fact]
    public void Validate_DoesNotTreatFolderContentAsAuthenticationError()
    {
        using var handler = new RecordingHandler(_ => JsonResponse(
            HttpStatusCode.OK,
            "{\"data\":{\"folders\":[{\"folderid\":\"root-test\",\"name\":\"unauthorized session notes\"}]}}"));
        using var httpClient = new HttpClient(handler);
        var validator = new O2SessionValidator(httpClient, "https://cloud.o2online.es/sapi/");

        Assert.Equal(O2SessionValidationStatus.Valid, validator.Validate(TestSession));
    }

    [Fact]
    public void Validate_TreatsServerFailureAsUnavailable()
    {
        using var handler = new RecordingHandler(_ => JsonResponse(HttpStatusCode.ServiceUnavailable, "{}"));
        using var httpClient = new HttpClient(handler);
        var validator = new O2SessionValidator(httpClient, "https://cloud.o2online.es/sapi/");

        Assert.Equal(O2SessionValidationStatus.Unavailable, validator.Validate(TestSession));
    }

    [Theory]
    [InlineData("https://cloud.o2online.es/media/file", true)]
    [InlineData("https://cdn.cloudfront.net/media/file", false)]
    [InlineData("https://upload.cloud.o2online.es/sapi/upload", false)]
    public void IsSameOrigin_RestrictsCookiesToApiOrigin(string requestUrl, bool expected)
    {
        Assert.Equal(
            expected,
            O2SessionRequest.IsSameOrigin(
                new Uri(requestUrl),
                new Uri("https://cloud.o2online.es/sapi/")));
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode statusCode, string body)
    {
        return new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(body),
        };
    }

    private sealed class RecordingHandler(Func<RequestSnapshot, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public List<RequestSnapshot> Requests { get; } = [];

        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var snapshot = RequestSnapshot.From(request);
            Requests.Add(snapshot);
            return responder(snapshot);
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(Send(request, cancellationToken));
        }
    }

    private sealed record RequestSnapshot(
        HttpMethod Method,
        Uri Uri,
        IReadOnlyDictionary<string, string> Headers)
    {
        public string Header(string name)
        {
            return Headers.TryGetValue(name, out var value) ? value : string.Empty;
        }

        public static RequestSnapshot From(HttpRequestMessage request)
        {
            var headers = request.Headers
                .ToDictionary(
                    pair => pair.Key,
                    pair => string.Join(", ", pair.Value),
                    StringComparer.OrdinalIgnoreCase);
            return new RequestSnapshot(request.Method, request.RequestUri!, headers);
        }
    }
}
