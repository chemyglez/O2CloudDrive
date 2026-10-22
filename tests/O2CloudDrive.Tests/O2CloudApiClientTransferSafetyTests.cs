using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json;
using O2CloudDrive.Api;
using O2CloudDrive.Auth;

namespace O2CloudDrive.Tests;

public sealed class O2CloudApiClientTransferSafetyTests
{
    [Fact]
    public void UploadFile_ConfirmsFilesWithoutListingUnrelatedFolders()
    {
        using var handler = new UploadHandler();
        using var http = new HttpClient(handler);
        var api = Client(http);

        var uploaded = api.UploadFile("root-test", "sample.txt", [1, 2, 3]);

        Assert.Equal("file-test", uploaded.Id);
        Assert.Equal(2, handler.Requests.Count);
        Assert.DoesNotContain(handler.Requests, uri => uri.AbsolutePath.EndsWith("/media/folder", StringComparison.Ordinal));
    }

    [Fact]
    public void UploadFile_ReadsResponseBodyWithCancellation()
    {
        using var body = new CancellationAwareContent();
        using var handler = new UploadHandler(body);
        using var http = new HttpClient(handler);

        Assert.Equal("file-test", Client(http).UploadFile("root-test", "sample.txt", [1, 2, 3]).Id);
        Assert.True(body.ReceivedCancelableToken);
    }

    [Fact]
    public void UploadFile_RejectsUnauthorizedHeadersWithoutReadingUntrustedBody()
    {
        using var body = new CancellationAwareContent(throwIfRead: true);
        using var handler = new UploadHandler(body, HttpStatusCode.Unauthorized);
        using var http = new HttpClient(handler);
        var auth = new FixedAuthService();
        var api = new O2CloudApiClient(http, auth, "https://cloud.o2online.es/sapi/");

        Assert.Throws<O2AuthenticationException>(() => api.UploadFile("root-test", "sample.txt", [1, 2, 3]));
        Assert.True(auth.Invalidated);
        Assert.False(body.ReadAttempted);
    }

    [Fact]
    public async Task MetadataRequest_CancelsWhileQueuedBehindAnotherRequest()
    {
        using var handler = new BlockingMetadataHandler();
        using var http = new HttpClient(handler);
        var api = Client(http);
        var first = Task.Run(api.GetRootFolder);
        Assert.True(handler.Started.Wait(TimeSpan.FromSeconds(2)));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        try
        {
            var queued = Task.Run(() =>
            {
                var error = Assert.Throws<TargetInvocationException>(() =>
                    typeof(O2CloudApiClient).GetMethod("SendJson", BindingFlags.Instance | BindingFlags.NonPublic)!
                        .Invoke(api, [HttpMethod.Get, "media/folder/root", new Dictionary<string, string?> { ["action"] = "get" }, null, cancellation.Token]));
                Assert.IsAssignableFrom<OperationCanceledException>(error.InnerException);
            });

            Assert.Same(queued, await Task.WhenAny(queued, Task.Delay(TimeSpan.FromSeconds(2))));
            await queued;
            Assert.Equal(1, handler.RequestCount);
        }
        finally
        {
            handler.Release.Set();
            await first;
        }
    }

    [Fact]
    public async Task UploadFile_LateUploadResponsePreservesNewerMetadataSession()
    {
        using var handler = new ConcurrentRotationHandler();
        using var http = new HttpClient(handler);
        var auth = new FixedAuthService();
        var api = new O2CloudApiClient(http, auth, "https://cloud.o2online.es/sapi/");
        var upload = Task.Run(() => api.UploadFile("root-test", "sample.txt", [1, 2, 3]));

        try
        {
            Assert.True(handler.UploadBodyStarted.Wait(TimeSpan.FromSeconds(2)));
            Assert.Equal("root-test", api.GetRootFolder().Id);
            Assert.Equal("metadata-key-456", auth.GetCurrentSession().ValidationKey);
        }
        finally
        {
            handler.ReleaseUploadBody.Set();
        }

        Assert.Equal("file-test", (await upload).Id);
        Assert.Equal("metadata-key-456", auth.GetCurrentSession().ValidationKey);
        Assert.All(handler.ConfirmationQueries, query => Assert.Contains("validationkey=metadata-key-456", query, StringComparison.Ordinal));
        Assert.NotEmpty(handler.ConfirmationQueries);
    }

    [Fact]
    public void DownloadFile_ClampsLastRangeAndChecksReturnedPosition()
    {
        using var handler = new RangeHandler(HttpStatusCode.PartialContent, [2, 3, 4], 2, 4, 5);
        using var http = new HttpClient(handler);

        var bytes = Client(http).DownloadFile(Item(5), 2, 8);

        Assert.Equal(new byte[] { 2, 3, 4 }, bytes);
        Assert.Equal("bytes=2-4", handler.RequestedRange);
        Assert.True(handler.RequestHadCancellation);
    }

    [Theory]
    [InlineData(0, 2, 3)]
    [InlineData(2, 4, 2)]
    public void DownloadFile_RejectsWrongPositionOrTruncatedBody(long from, long to, int bodyLength)
    {
        using var handler = new RangeHandler(HttpStatusCode.PartialContent, new byte[bodyLength], from, to, 5);
        using var http = new HttpClient(handler);

        Assert.Throws<HttpRequestException>(() => Client(http).DownloadFile(Item(5), 2, 3));
    }

    [Fact]
    public void DownloadFile_RejectsIgnoredRangeInsteadOfReturningWrongBytes()
    {
        using var handler = new RangeHandler(HttpStatusCode.OK, new byte[5]);
        using var http = new HttpClient(handler);

        Assert.Throws<IOException>(() => Client(http).DownloadFile(Item(5), 2, 3));
    }

    [Fact]
    public void DownloadFile_AcceptsWholeSmallFileWithoutRangeSupport()
    {
        using var handler = new RangeHandler(HttpStatusCode.OK, [1, 2, 3]);
        using var http = new HttpClient(handler);

        Assert.Equal(new byte[] { 1, 2, 3 }, Client(http).DownloadFile(Item(3), 0, 8));
    }

    private static O2CloudApiClient Client(HttpClient http) =>
        new(http, new FixedAuthService(), "https://cloud.o2online.es/sapi/");

    private static O2CloudItemDto Item(long size) =>
        new("file-test", "sample.txt", "root-test", false, size, null, "https://download.example.test/sample.txt", null, null, null, null);

    private sealed class FixedAuthService : IAuthService
    {
        private O2Session _session = new() { ValidationKey = "test-key" };
        public bool Invalidated { get; private set; }
        public O2Session GetCurrentSession() => _session;
        public bool HasStoredSession() => true;
        public bool HasValidatedSession => true;
        public O2Session? EnsureAuthenticated(bool allowInteractive, bool forceInteractive = false) => _session;
        public void InvalidateSession() => Invalidated = true;
        public bool InvalidateSessionIfCurrent(O2Session expectedSession) { Invalidated = true; return true; }
        public bool RefreshValidationKeyIfCurrent(O2Session expectedSession, string validationKey) =>
            ReferenceEquals(Interlocked.CompareExchange(ref _session, expectedSession with { ValidationKey = validationKey }, expectedSession), expectedSession);
        public void Logout() { }
    }

    private sealed class UploadHandler(HttpContent? uploadBody = null, HttpStatusCode uploadStatus = HttpStatusCode.OK) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            return request.Content is MultipartFormDataContent
                ? new HttpResponseMessage(uploadStatus) { Content = uploadBody ?? new StringContent("{\"data\":{\"media\":{\"id\":\"file-test\",\"name\":\"sample.txt\",\"size\":3}}}") }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"data\":{\"media\":[{\"id\":\"file-test\",\"name\":\"sample.txt\",\"folderid\":\"root-test\",\"size\":3}]}}") };
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(Send(request, cancellationToken));
    }

    private sealed class CancellationAwareContent(bool throwIfRead = false) : HttpContent
    {
        public bool ReadAttempted { get; private set; }
        public bool ReceivedCancelableToken { get; private set; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            ReadAttempted = true;
            if (throwIfRead)
            {
                throw new InvalidOperationException("Unexpected response body read.");
            }

            ReceivedCancelableToken = cancellationToken.CanBeCanceled;
            var body = Encoding.UTF8.GetBytes("{\"data\":{\"media\":{\"id\":\"file-test\",\"name\":\"sample.txt\",\"size\":3}}}");
            await stream.WriteAsync(body, cancellationToken);
        }

        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }

    private sealed class BlockingMetadataHandler : HttpMessageHandler
    {
        public ManualResetEventSlim Started { get; } = new(false);
        public ManualResetEventSlim Release { get; } = new(false);
        public int RequestCount { get; private set; }

        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            Started.Set();
            Release.Wait(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"data\":{\"folders\":[{\"folderid\":\"root-test\"}]}}") };
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(Send(request, cancellationToken));

        protected override void Dispose(bool disposing)
        {
            if (disposing) { Started.Dispose(); Release.Dispose(); }
            base.Dispose(disposing);
        }
    }

    private sealed class RangeHandler(HttpStatusCode status, byte[] body, long? from = null, long? to = null, long? total = null) : HttpMessageHandler
    {
        public string? RequestedRange { get; private set; }
        public bool RequestHadCancellation { get; private set; }

        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.Host == "cloud.o2online.es")
            {
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("{}") };
            }

            RequestedRange = request.Headers.Range?.ToString();
            RequestHadCancellation = cancellationToken.CanBeCanceled;
            var response = new HttpResponseMessage(status) { Content = new ByteArrayContent(body) };
            if (from is not null)
            {
                response.Content.Headers.ContentRange = new ContentRangeHeaderValue(from.Value, to!.Value, total!.Value);
            }
            return response;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(Send(request, cancellationToken));
    }

    private sealed class ConcurrentRotationHandler : HttpMessageHandler
    {
        public ManualResetEventSlim UploadBodyStarted { get; } = new(false);
        public ManualResetEventSlim ReleaseUploadBody { get; } = new(false);
        public List<string> ConfirmationQueries { get; } = [];

        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Content is MultipartFormDataContent)
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new DelayedUploadContent(UploadBodyStarted, ReleaseUploadBody) };
            }

            if (request.RequestUri!.AbsolutePath.EndsWith("/media/folder/root", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"data\":{\"folders\":[{\"folderid\":\"root-test\"}]},\"validationKey\":\"metadata-key-456\"}") };
            }

            ConfirmationQueries.Add(request.RequestUri.Query);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"data\":{\"media\":[{\"id\":\"file-test\",\"name\":\"sample.txt\",\"folderid\":\"root-test\",\"size\":3}]}}") };
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(Send(request, cancellationToken));

        protected override void Dispose(bool disposing)
        {
            if (disposing) { UploadBodyStarted.Dispose(); ReleaseUploadBody.Dispose(); }
            base.Dispose(disposing);
        }
    }

    private sealed class DelayedUploadContent(ManualResetEventSlim started, ManualResetEventSlim release) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            started.Set();
            release.Wait(cancellationToken);
            var body = Encoding.UTF8.GetBytes("{\"data\":{\"media\":{\"id\":\"file-test\",\"name\":\"sample.txt\",\"size\":3}},\"validationKey\":\"upload-key-123\"}");
            return stream.WriteAsync(body, cancellationToken).AsTask();
        }

        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }
}
