using System.Net;
using O2CloudDrive.Api;
using O2CloudDrive.Auth;

namespace O2CloudDrive.Tests;

public sealed class O2CloudApiClientAuthTests
{
    [Fact]
    public void GetRootFolder_UsesGetWithoutSyntheticDeviceId()
    {
        var auth = new FixedAuthService(new O2Session
        {
            ValidationKey = "current-key-123",
            Cookies = new Dictionary<string, string> { ["session-cookie"] = "cookie-value" },
            UserAgent = "O2CloudDrive.Tests/1.0",
        });
        var handler = new StatusHandler(
            HttpStatusCode.OK,
            "{\"data\":{\"folders\":[{\"folderid\":\"root-test\",\"name\":\"\"}]}}");
        using var httpClient = new HttpClient(handler);
        var api = new O2CloudApiClient(httpClient, auth, "https://cloud.o2online.es/sapi/");

        var root = api.GetRootFolder();

        Assert.Equal("root-test", root.Id);
        Assert.Equal(HttpMethod.Get, handler.LastMethod);
        Assert.EndsWith("/sapi/media/folder/root", handler.LastUri?.AbsolutePath, StringComparison.Ordinal);
        Assert.Contains("action=get", handler.LastUri?.Query, StringComparison.Ordinal);
        Assert.Contains("validationkey=current-key-123", handler.LastUri?.Query, StringComparison.Ordinal);
        Assert.Equal("session-cookie=cookie-value", handler.LastHeader("Cookie"));
        Assert.Equal("O2CloudDrive.Tests/1.0", handler.LastHeader("User-Agent"));
        Assert.Equal("https://cloud.o2online.es", handler.LastHeader("Origin"));
        Assert.Equal("https://cloud.o2online.es/", handler.LastHeader("Referer"));
        Assert.False(handler.LastHeaders?.ContainsKey("X-deviceid"));
    }

    [Theory]
    [InlineData("{\"folders\":[{\"folderid\":\"root-top-folders\"}]}", "root-top-folders")]
    [InlineData("{\"data\":{\"folder\":[{\"folderid\":\"root-data-folder\"}]}}", "root-data-folder")]
    [InlineData("{\"folder\":[{\"folderid\":\"root-top-folder\"}]}", "root-top-folder")]
    [InlineData("{\"data\":{\"items\":[{\"id\":\"file-1\",\"type\":\"file\",\"size\":12},{\"folderid\":\"root-data-items\",\"type\":\"folder\"}]}}", "root-data-items")]
    [InlineData("{\"items\":[{\"folderid\":\"root-top-items\",\"name\":\"\"}]}", "root-top-items")]
    [InlineData("{\"data\":{\"children\":[{\"folderid\":\"root-data-children\",\"kind\":\"directory\"}]}}", "root-data-children")]
    [InlineData("{\"children\":[{\"folderid\":\"root-top-children\",\"name\":\"\"}]}", "root-top-children")]
    public void GetRootFolder_AcceptsAndroidRootResponseShapes(string responseBody, string expectedId)
    {
        var auth = new FixedAuthService(new O2Session { ValidationKey = "current-key-123" });
        using var httpClient = new HttpClient(new StatusHandler(HttpStatusCode.OK, responseBody));
        var api = new O2CloudApiClient(httpClient, auth, "https://cloud.o2online.es/sapi/");

        Assert.Equal(expectedId, api.GetRootFolder().Id);
    }

    [Fact]
    public void GetRootFolder_InvalidatesRejectedSessionWithoutLeakingValidationKey()
    {
        const string validationKey = "secret-key-123";
        var auth = new FixedAuthService(new O2Session { ValidationKey = validationKey });
        using var httpClient = new HttpClient(new UnauthorizedHandler());
        var api = new O2CloudApiClient(httpClient, auth, "https://cloud.o2online.es/sapi/");

        var error = Assert.Throws<O2AuthenticationException>(() => api.GetRootFolder());

        Assert.True(auth.Invalidated);
        Assert.Equal(HttpStatusCode.Unauthorized, error.StatusCode);
        Assert.DoesNotContain(validationKey, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("validationkey", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetRootFolder_DoesNotInvalidateSessionForForbiddenResponse()
    {
        var auth = new FixedAuthService(new O2Session { ValidationKey = "secret-key-123" });
        using var httpClient = new HttpClient(new StatusHandler(HttpStatusCode.Forbidden, "{}"));
        var api = new O2CloudApiClient(httpClient, auth, "https://cloud.o2online.es/sapi/");

        var error = Assert.Throws<HttpRequestException>(() => api.GetRootFolder());

        Assert.False(auth.Invalidated);
        Assert.Equal(HttpStatusCode.Forbidden, error.StatusCode);
    }

    [Fact]
    public void GetRootFolder_InvalidatesExplicitAuthenticationErrorInSuccessfulResponse()
    {
        var auth = new FixedAuthService(new O2Session { ValidationKey = "secret-key-123" });
        using var httpClient = new HttpClient(new StatusHandler(HttpStatusCode.OK, "{\"error\":\"unauthorized\"}"));
        var api = new O2CloudApiClient(httpClient, auth, "https://cloud.o2online.es/sapi/");

        Assert.Throws<O2AuthenticationException>(() => api.GetRootFolder());
        Assert.True(auth.Invalidated);
    }

    [Fact]
    public void GetRootFolder_DoesNotPersistOrExposeKeyFromErrorPayload()
    {
        const string untrustedKey = "untrusted-secret-key-456";
        var auth = new FixedAuthService(new O2Session { ValidationKey = "current-key-123" });
        using var httpClient = new HttpClient(new StatusHandler(
            HttpStatusCode.OK,
            $"{{\"success\":false,\"validationKey\":\"{untrustedKey}\"}}"));
        var api = new O2CloudApiClient(httpClient, auth, "https://cloud.o2online.es/sapi/");

        var error = Assert.Throws<InvalidOperationException>(() => api.GetRootFolder());

        Assert.Equal("current-key-123", auth.GetCurrentSession().ValidationKey);
        Assert.DoesNotContain(untrustedKey, error.Message, StringComparison.Ordinal);
        Assert.Contains("[redacted]", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateFolder_PropagatesAuthenticationRejectionWithoutTryingFallbacks()
    {
        var auth = new FixedAuthService(new O2Session { ValidationKey = "secret-key-123" });
        var handler = new StatusHandler(HttpStatusCode.Unauthorized, "{\"error\":\"unauthorized\"}");
        using var httpClient = new HttpClient(handler);
        var api = new O2CloudApiClient(httpClient, auth, "https://cloud.o2online.es/sapi/");

        Assert.Throws<O2AuthenticationException>(() => api.CreateFolder("root-test", "Nueva carpeta"));
        Assert.True(auth.Invalidated);
        Assert.Equal(1, handler.SendCount);
    }

    [Fact]
    public async Task GetRootFolder_LateUnauthorizedResponseDoesNotInvalidateNewSession()
    {
        var oldSession = new O2Session { ValidationKey = "old-secret-key-123" };
        var newSession = new O2Session { ValidationKey = "new-secret-key-123" };
        var auth = new FixedAuthService(oldSession);
        using var handler = new BlockingUnauthorizedHandler();
        using var httpClient = new HttpClient(handler);
        var api = new O2CloudApiClient(httpClient, auth, "https://cloud.o2online.es/sapi/");

        var request = Task.Run(() => Assert.Throws<O2AuthenticationException>(() => api.GetRootFolder()));
        Assert.True(handler.RequestStarted.Wait(TimeSpan.FromSeconds(2)));
        auth.ReplaceSession(newSession);
        handler.ReleaseResponse.Set();
        await request;

        Assert.False(auth.Invalidated);
        Assert.Same(newSession, auth.GetCurrentSession());
    }

    [Fact]
    public void GetRootFolder_UsesRotatedKeyOnNextApiRequest()
    {
        var auth = new FixedAuthService(new O2Session { ValidationKey = "old-key-123" });
        using var handler = new SequenceHandler(
            "{\"data\":{\"folders\":[{\"folderid\":\"root-test\"}]},\"validationKey\":\"fresh-key-456\"}",
            "{\"data\":{\"folders\":[{\"folderid\":\"root-test\"}]}}");
        using var httpClient = new HttpClient(handler);
        var api = new O2CloudApiClient(httpClient, auth, "https://cloud.o2online.es/sapi/");

        api.GetRootFolder();
        api.GetRootFolder();

        Assert.Equal(2, handler.Uris.Count);
        Assert.Contains("validationkey=old-key-123", handler.Uris[0].Query, StringComparison.Ordinal);
        Assert.Contains("validationkey=fresh-key-456", handler.Uris[1].Query, StringComparison.Ordinal);
        Assert.Equal("fresh-key-456", auth.GetCurrentSession().ValidationKey);
    }

    [Fact]
    public void UploadFile_PropagatesRotatedKeyBeforeConfirmingTheUpload()
    {
        var auth = new FixedAuthService(new O2Session { ValidationKey = "old-key-123" });
        using var handler = new UploadRotationHandler();
        using var httpClient = new HttpClient(handler);
        var api = new O2CloudApiClient(httpClient, auth, "https://cloud.o2online.es/sapi/");

        var uploaded = api.UploadFile("root-test", "sample.txt", [1, 2, 3]);

        Assert.Equal("file-test", uploaded.Id);
        Assert.Equal("fresh-key-456", auth.GetCurrentSession().ValidationKey);
        Assert.Contains("validationkey=old-key-123", handler.Uris[0].Query, StringComparison.Ordinal);
        Assert.All(
            handler.Uris.Skip(1),
            uri => Assert.Contains("validationkey=fresh-key-456", uri.Query, StringComparison.Ordinal));
    }

    private sealed class FixedAuthService(O2Session session) : IAuthService
    {
        private O2Session _session = session;
        public bool Invalidated { get; private set; }

        public O2Session GetCurrentSession() => Invalidated ? new O2Session() : _session;
        public bool HasStoredSession() => !Invalidated;
        public bool HasValidatedSession => !Invalidated;
        public O2Session? EnsureAuthenticated(bool allowInteractive, bool forceInteractive = false) => GetCurrentSession();

        public void InvalidateSession()
        {
            Invalidated = true;
        }

        public bool InvalidateSessionIfCurrent(O2Session expectedSession)
        {
            if (Invalidated || !ReferenceEquals(_session, expectedSession))
            {
                return false;
            }

            Invalidated = true;
            return true;
        }

        public bool RefreshValidationKeyIfCurrent(O2Session expectedSession, string validationKey)
        {
            if (Invalidated || !ReferenceEquals(_session, expectedSession))
            {
                return false;
            }

            _session = expectedSession with { ValidationKey = validationKey };
            return true;
        }

        public void ReplaceSession(O2Session replacement)
        {
            _session = replacement;
            Invalidated = false;
        }

        public void Logout()
        {
            Invalidated = true;
        }
    }

    private sealed class UnauthorizedHandler : HttpMessageHandler
    {
        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent("{\"error\":\"unauthorized\"}"),
            };
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(Send(request, cancellationToken));
        }
    }

    private sealed class StatusHandler(HttpStatusCode statusCode, string content) : HttpMessageHandler
    {
        public int SendCount { get; private set; }
        public HttpMethod? LastMethod { get; private set; }
        public Uri? LastUri { get; private set; }
        public IReadOnlyDictionary<string, string>? LastHeaders { get; private set; }

        public string LastHeader(string name)
        {
            return LastHeaders?.TryGetValue(name, out var value) == true ? value : string.Empty;
        }

        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            SendCount++;
            LastMethod = request.Method;
            LastUri = request.RequestUri;
            LastHeaders = request.Headers.ToDictionary(
                pair => pair.Key,
                pair => string.Join(", ", pair.Value),
                StringComparer.OrdinalIgnoreCase);
            return new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(content),
            };
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(Send(request, cancellationToken));
        }
    }

    private sealed class BlockingUnauthorizedHandler : HttpMessageHandler
    {
        public ManualResetEventSlim RequestStarted { get; } = new(false);
        public ManualResetEventSlim ReleaseResponse { get; } = new(false);

        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestStarted.Set();
            ReleaseResponse.Wait(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent("{\"error\":\"unauthorized\"}"),
            };
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(Send(request, cancellationToken));
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                RequestStarted.Dispose();
                ReleaseResponse.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    private sealed class SequenceHandler(params string[] bodies) : HttpMessageHandler
    {
        private int _index;
        public List<Uri> Uris { get; } = [];

        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Uris.Add(request.RequestUri!);
            var body = bodies[Math.Min(_index, bodies.Length - 1)];
            _index++;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body),
            };
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(Send(request, cancellationToken));
        }
    }

    private sealed class UploadRotationHandler : HttpMessageHandler
    {
        public List<Uri> Uris { get; } = [];

        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Uris.Add(request.RequestUri!);
            var body = request.Content is MultipartFormDataContent
                ? "{\"data\":{\"media\":{\"id\":\"file-test\",\"name\":\"sample.txt\",\"size\":3}},\"validationKey\":\"fresh-key-456\"}"
                : request.RequestUri?.AbsolutePath.EndsWith("/media/folder", StringComparison.OrdinalIgnoreCase) == true
                    ? "{\"data\":{\"folders\":[]}}"
                    : "{\"data\":{\"media\":[{\"id\":\"file-test\",\"name\":\"sample.txt\",\"folderid\":\"root-test\",\"size\":3}]}}";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body),
            };
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(Send(request, cancellationToken));
        }
    }
}
