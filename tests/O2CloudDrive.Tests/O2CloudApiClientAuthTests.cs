using System.Net;
using O2CloudDrive.Api;
using O2CloudDrive.Auth;

namespace O2CloudDrive.Tests;

public sealed class O2CloudApiClientAuthTests
{
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

        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            SendCount++;
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
}
