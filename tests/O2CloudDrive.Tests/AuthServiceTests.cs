using System.Net;
using O2CloudDrive.Auth;

namespace O2CloudDrive.Tests;

public sealed class AuthServiceTests
{
    private static readonly O2Session CandidateSession = new()
    {
        ValidationKey = "candidate-key-123",
    };

    [Fact]
    public void GetCurrentSession_DoesNotExposeUnvalidatedStoredCredential()
    {
        var store = new MemorySessionStore { StoredSession = CandidateSession };
        using var httpClient = new HttpClient(new UnauthorizedHandler());
        var validator = new O2SessionValidator(httpClient, "https://cloud.o2online.es/sapi/");
        var auth = new AuthService(store, new FixedLoginService(null), validator);

        Assert.False(auth.GetCurrentSession().IsAuthenticated);
        Assert.True(auth.HasStoredSession());
    }

    [Fact]
    public void EnsureAuthenticated_DeletesStoredCredentialWhenO2RejectsIt()
    {
        var store = new MemorySessionStore { StoredSession = CandidateSession };
        var login = new FixedLoginService(null);
        using var httpClient = new HttpClient(new UnauthorizedHandler());
        var validator = new O2SessionValidator(httpClient, "https://cloud.o2online.es/sapi/");
        var auth = new AuthService(store, login, validator);

        Assert.Null(auth.EnsureAuthenticated(allowInteractive: false));
        Assert.Null(store.StoredSession);
        Assert.Equal(1, store.DeleteCount);
        Assert.Equal(0, login.LoginCount);
    }

    [Fact]
    public void EnsureAuthenticated_PreservesStoredCredentialWhenValidationIsUnavailable()
    {
        var store = new MemorySessionStore { StoredSession = CandidateSession };
        var login = new FixedLoginService(null);
        using var httpClient = new HttpClient(new UnavailableHandler());
        var validator = new O2SessionValidator(httpClient, "https://cloud.o2online.es/sapi/");
        var auth = new AuthService(store, login, validator);

        Assert.Throws<O2SessionUnavailableException>(() => auth.EnsureAuthenticated(allowInteractive: true));
        Assert.Same(CandidateSession, store.StoredSession);
        Assert.Equal(0, store.DeleteCount);
        Assert.Equal(0, login.LoginCount);
    }

    [Fact]
    public void EnsureAuthenticated_DoesNotRevalidateCurrentInteractiveSession()
    {
        var store = new MemorySessionStore();
        var login = new FixedLoginService(CandidateSession);
        var handler = new UnavailableHandler();
        using var httpClient = new HttpClient(handler);
        var validator = new O2SessionValidator(httpClient, "https://cloud.o2online.es/sapi/");
        var auth = new AuthService(store, login, validator);

        Assert.Same(CandidateSession, auth.EnsureAuthenticated(allowInteractive: true, forceInteractive: true));
        Assert.Same(CandidateSession, auth.EnsureAuthenticated(allowInteractive: false));
        Assert.Equal(0, handler.SendCount);
    }

    [Fact]
    public void EnsureAuthenticated_PersistsRotatedKeyFromStoredSessionValidation()
    {
        var store = new MemorySessionStore { StoredSession = CandidateSession };
        using var httpClient = new HttpClient(new RotatingValidHandler());
        var validator = new O2SessionValidator(httpClient, "https://cloud.o2online.es/sapi/");
        var auth = new AuthService(store, new FixedLoginService(null), validator);

        var session = auth.EnsureAuthenticated(allowInteractive: false);

        Assert.NotNull(session);
        Assert.Equal("rotated-key-456", session.ValidationKey);
        Assert.Equal("rotated-key-456", store.StoredSession?.ValidationKey);
        Assert.Equal(1, store.SaveCount);
    }

    [Fact]
    public void RefreshValidationKey_DoesNotLetOlderResponseOverwriteNewSession()
    {
        var store = new MemorySessionStore();
        using var httpClient = new HttpClient(new UnavailableHandler());
        var auth = new AuthService(
            store,
            new FixedLoginService(CandidateSession),
            new O2SessionValidator(httpClient, "https://cloud.o2online.es/sapi/"));
        var original = auth.EnsureAuthenticated(allowInteractive: true, forceInteractive: true)!;

        Assert.True(auth.RefreshValidationKeyIfCurrent(original, "fresh-key-456"));
        Assert.False(auth.RefreshValidationKeyIfCurrent(original, "late-stale-key-789"));
        Assert.Equal("fresh-key-456", auth.GetCurrentSession().ValidationKey);
        Assert.Equal("fresh-key-456", store.StoredSession?.ValidationKey);
    }

    [Fact]
    public async Task EnsureAuthenticated_DoesNotRestoreSessionInvalidatedDuringValidation()
    {
        var store = new MemorySessionStore { StoredSession = CandidateSession };
        var login = new FixedLoginService(null);
        using var handler = new BlockingValidHandler();
        using var httpClient = new HttpClient(handler);
        var validator = new O2SessionValidator(httpClient, "https://cloud.o2online.es/sapi/");
        var auth = new AuthService(store, login, validator);

        var validation = Task.Run(() => auth.EnsureAuthenticated(allowInteractive: false));
        Assert.True(handler.RequestStarted.Wait(TimeSpan.FromSeconds(2)));
        auth.InvalidateSession();
        handler.ReleaseResponse.Set();

        Assert.Null(await validation);
        Assert.False(auth.HasValidatedSession);
        Assert.Null(store.StoredSession);
    }

    [Fact]
    public async Task EnsureAuthenticated_SerializesConcurrentStoredSessionValidation()
    {
        var store = new MemorySessionStore { StoredSession = CandidateSession };
        using var handler = new ConcurrentValidationHandler();
        using var httpClient = new HttpClient(handler);
        var validator = new O2SessionValidator(httpClient, "https://cloud.o2online.es/sapi/");
        var auth = new AuthService(store, new FixedLoginService(null), validator);

        var first = Task.Run(() => auth.EnsureAuthenticated(allowInteractive: false));
        Assert.True(handler.FirstRequestStarted.Wait(TimeSpan.FromSeconds(2)));
        var second = Task.Run(() => auth.EnsureAuthenticated(allowInteractive: false));

        var sessions = await Task.WhenAll(first, second);

        Assert.Equal(1, handler.SendCount);
        Assert.Equal(0, store.DeleteCount);
        Assert.All(sessions, session => Assert.Equal("rotated-key-456", session?.ValidationKey));
        Assert.Equal("rotated-key-456", store.StoredSession?.ValidationKey);
    }

    private sealed class MemorySessionStore : ISessionStore
    {
        public O2Session? StoredSession { get; set; }
        public int SaveCount { get; private set; }
        public int DeleteCount { get; private set; }

        public O2Session? TryRead() => StoredSession;

        public void Save(O2Session session)
        {
            StoredSession = session;
            SaveCount++;
        }

        public void Delete()
        {
            StoredSession = null;
            DeleteCount++;
        }
    }

    private sealed class FixedLoginService(O2Session? session) : IInteractiveLoginService
    {
        public int LoginCount { get; private set; }

        public O2Session? Login(O2SessionValidator validator)
        {
            LoginCount++;
            return session;
        }

        public void ClearSessionCache()
        {
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

    private sealed class UnavailableHandler : HttpMessageHandler
    {
        public int SendCount { get; private set; }

        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            SendCount++;
            throw new HttpRequestException("Temporary DNS failure.");
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            SendCount++;
            return Task.FromException<HttpResponseMessage>(new HttpRequestException("Temporary DNS failure."));
        }
    }

    private sealed class BlockingValidHandler : HttpMessageHandler
    {
        public ManualResetEventSlim RequestStarted { get; } = new(false);
        public ManualResetEventSlim ReleaseResponse { get; } = new(false);

        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestStarted.Set();
            ReleaseResponse.Wait(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"data\":{\"folders\":[{\"folderid\":\"root-test\",\"name\":\"\"}]}}"),
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

    private sealed class RotatingValidHandler : HttpMessageHandler
    {
        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"data\":{\"folders\":[{\"folderid\":\"root-test\"}],\"validationKey\":\"rotated-key-456\"}}"),
            };
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(Send(request, cancellationToken));
        }
    }

    private sealed class ConcurrentValidationHandler : HttpMessageHandler
    {
        private readonly ManualResetEventSlim _secondRequestStarted = new(false);
        private int _sendCount;

        public ManualResetEventSlim FirstRequestStarted { get; } = new(false);
        public int SendCount => Volatile.Read(ref _sendCount);

        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var requestNumber = Interlocked.Increment(ref _sendCount);
            if (requestNumber == 1)
            {
                FirstRequestStarted.Set();
                _secondRequestStarted.Wait(TimeSpan.FromMilliseconds(250), cancellationToken);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "{\"data\":{\"folders\":[{\"folderid\":\"root-test\"}],\"validationKey\":\"rotated-key-456\"}}"),
                };
            }

            _secondRequestStarted.Set();
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
                FirstRequestStarted.Dispose();
                _secondRequestStarted.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
