using System.Net;
using System.Reflection;
using System.Text.Json;
using O2CloudDrive.Api;
using O2CloudDrive.Auth;

namespace O2CloudDrive.Tests;

public sealed class O2CloudApiClientConfirmationTests
{
    [Fact]
    public void FindChild_DoesNotAcceptSameNameWhenExpectedSizeDiffers()
    {
        var api = CreateClient([
            new ListedFile("old-id", "overwrite-probe.txt", "root-test", 133),
        ]);

        var match = InvokeFindChild(
            api,
            "root-test",
            "overwrite-probe.txt",
            expectedSize: 149,
            expectedId: null);

        Assert.Null(match);
    }

    [Fact]
    public void FindChild_DoesNotAcceptDifferentIdWhenExactIdIsExpected()
    {
        var api = CreateClient([
            new ListedFile("different-id", "overwrite-probe.txt", "root-test", 149),
        ]);

        var match = InvokeFindChild(
            api,
            "root-test",
            "overwrite-probe.txt",
            expectedSize: 149,
            expectedId: "expected-id");

        Assert.Null(match);
    }

    [Fact]
    public void FindChild_UniqueNameFallbackRejectsHomonyms()
    {
        var api = CreateClient([
            new ListedFile("first-id", "~o2cd-new-test.txt", "root-test", 149),
            new ListedFile("second-id", "~o2cd-new-test.txt", "root-test", 149),
        ]);

        var match = InvokeFindChildCore(
            api,
            "root-test",
            "~o2cd-new-test.txt",
            expectedSize: 149,
            expectedId: "upload-job-id",
            allowUniqueNameFallback: true);

        Assert.Null(match);
    }

    [Fact]
    public void ReplaceFile_UploadsTemporaryCopyThenPromotesItAndTrashesPreviousObject()
    {
        using var handler = new ReplacementTransactionHandler();
        using var httpClient = new HttpClient(handler);
        var api = new O2CloudApiClient(
            httpClient,
            new FixedAuthService(new O2Session { ValidationKey = "test-key" }),
            "https://cloud.o2online.es/sapi/");
        var previous = Item("old-id", "overwrite-probe.txt", 133);
        var replacementBytes = Enumerable.Repeat((byte)'v', 149).ToArray();
        using var replacement = new MemoryStream(replacementBytes, writable: false);

        var result = api.ReplaceFile(
            previous,
            "root-test",
            "overwrite-probe.txt",
            replacement,
            replacementBytes.LongLength);

        Assert.Equal("new-id", result.Id);
        Assert.Equal("overwrite-probe.txt", result.Name);
        Assert.Equal(149, result.Size);

        var active = Assert.Single(handler.ActiveFiles);
        Assert.Equal("new-id", active.Id);
        Assert.Equal("overwrite-probe.txt", active.Name);
        Assert.Equal(149, active.Size);

        var trashed = Assert.Single(handler.TrashedFiles);
        Assert.Equal("old-id", trashed.Id);
        Assert.StartsWith("~o2cd-old-", trashed.Name, StringComparison.Ordinal);

        Assert.Collection(
            handler.Mutations,
            operation => Assert.StartsWith("upload:new-id:~o2cd-new-", operation, StringComparison.Ordinal),
            operation => Assert.StartsWith("rename:old-id:~o2cd-old-", operation, StringComparison.Ordinal),
            operation => Assert.Equal("rename:new-id:overwrite-probe.txt", operation),
            operation => Assert.Equal("trash:old-id", operation));
    }

    [Fact]
    public void ReplaceFile_CanonicalizesStalePreviousSizeBeforeRenamingOriginal()
    {
        using var handler = new ReplacementTransactionHandler();
        using var httpClient = new HttpClient(handler);
        var api = new O2CloudApiClient(
            httpClient,
            new FixedAuthService(new O2Session { ValidationKey = "test-key" }),
            "https://cloud.o2online.es/sapi/");
        var stalePrevious = Item("old-id", "overwrite-probe.txt", 149);
        var replacementBytes = Enumerable.Repeat((byte)'v', 149).ToArray();
        using var replacement = new MemoryStream(replacementBytes, writable: false);

        var result = api.ReplaceFile(
            stalePrevious,
            "root-test",
            "overwrite-probe.txt",
            replacement,
            replacementBytes.LongLength);

        Assert.Equal("new-id", result.Id);
        var trashed = Assert.Single(handler.TrashedFiles);
        Assert.Equal("old-id", trashed.Id);
        Assert.Equal(133, trashed.Size);
    }

    [Fact]
    public void ReplaceFile_WhenUploadReturnsNonCanonicalId_UsesUniqueTemporaryNameAndSize()
    {
        using var handler = new ReplacementTransactionHandler
        {
            ReturnNonCanonicalUploadId = true,
        };
        using var httpClient = new HttpClient(handler);
        var api = new O2CloudApiClient(
            httpClient,
            new FixedAuthService(new O2Session { ValidationKey = "test-key" }),
            "https://cloud.o2online.es/sapi/");
        var previous = Item("old-id", "overwrite-probe.txt", 133);
        var replacementBytes = Enumerable.Repeat((byte)'v', 149).ToArray();
        using var replacement = new MemoryStream(replacementBytes, writable: false);

        var result = api.ReplaceFile(
            previous,
            "root-test",
            "overwrite-probe.txt",
            replacement,
            replacementBytes.LongLength);

        Assert.Equal("new-id", result.Id);
        Assert.DoesNotContain(handler.ActiveFiles, file => file.Id == "upload-job-id");
        var active = Assert.Single(handler.ActiveFiles);
        Assert.Equal("overwrite-probe.txt", active.Name);
        Assert.Equal(149, active.Size);
    }

    [Fact]
    public void ReplaceFile_WhenPromotionFails_RestoresPreviousObjectAndTrashesTemporaryCopy()
    {
        using var handler = new ReplacementTransactionHandler
        {
            FailPromotion = true,
        };
        using var httpClient = new HttpClient(handler);
        var api = new O2CloudApiClient(
            httpClient,
            new FixedAuthService(new O2Session { ValidationKey = "test-key" }),
            "https://cloud.o2online.es/sapi/");
        var previous = Item("old-id", "overwrite-probe.txt", 133);
        var replacementBytes = Enumerable.Repeat((byte)'v', 149).ToArray();
        using var replacement = new MemoryStream(replacementBytes, writable: false);

        Assert.Throws<IOException>(() => api.ReplaceFile(
            previous,
            "root-test",
            "overwrite-probe.txt",
            replacement,
            replacementBytes.LongLength));

        var active = Assert.Single(handler.ActiveFiles);
        Assert.Equal("old-id", active.Id);
        Assert.Equal("overwrite-probe.txt", active.Name);
        Assert.Equal(133, active.Size);

        var trashed = Assert.Single(handler.TrashedFiles);
        Assert.Equal("new-id", trashed.Id);
        Assert.StartsWith("~o2cd-new-", trashed.Name, StringComparison.Ordinal);
    }

    [Fact]
    public void ReplaceFile_WhenBackupCleanupFails_KeepsReplacementCommittedAndReportsWarning()
    {
        using var handler = new ReplacementTransactionHandler
        {
            FailBackupTrash = true,
        };
        using var httpClient = new HttpClient(handler);
        var api = new O2CloudApiClient(
            httpClient,
            new FixedAuthService(new O2Session { ValidationKey = "test-key" }),
            "https://cloud.o2online.es/sapi/");
        var progress = new List<O2TransferProgress>();
        api.TransferProgress += (_, update) => progress.Add(update);
        var previous = Item("old-id", "overwrite-probe.txt", 133);
        var replacementBytes = Enumerable.Repeat((byte)'v', 149).ToArray();
        using var replacement = new MemoryStream(replacementBytes, writable: false);

        var result = api.ReplaceFile(
            previous,
            "root-test",
            "overwrite-probe.txt",
            replacement,
            replacementBytes.LongLength);

        Assert.Equal("new-id", result.Id);
        Assert.Empty(handler.TrashedFiles);
        Assert.Contains(handler.ActiveFiles, file =>
            file.Id == "new-id" && file.Name == "overwrite-probe.txt" && file.Size == 149);
        Assert.Contains(handler.ActiveFiles, file =>
            file.Id == "old-id" && file.Name.StartsWith("~o2cd-old-", StringComparison.Ordinal));
        var warning = Assert.Single(progress, update => update.Phase == "warning");
        Assert.Equal("overwrite-probe.txt", warning.FileName);
    }

    [Fact]
    public void ReplaceFile_WhenProgressSubscriberThrows_DoesNotChangeCommittedResult()
    {
        using var handler = new ReplacementTransactionHandler();
        using var httpClient = new HttpClient(handler);
        var api = new O2CloudApiClient(
            httpClient,
            new FixedAuthService(new O2Session { ValidationKey = "test-key" }),
            "https://cloud.o2online.es/sapi/");
        api.TransferProgress += (_, _) => throw new InvalidOperationException("Injected subscriber failure.");
        var previous = Item("old-id", "overwrite-probe.txt", 133);
        var replacementBytes = Enumerable.Repeat((byte)'v', 149).ToArray();
        using var replacement = new MemoryStream(replacementBytes, writable: false);

        var result = api.ReplaceFile(
            previous,
            "root-test",
            "overwrite-probe.txt",
            replacement,
            replacementBytes.LongLength);

        Assert.Equal("new-id", result.Id);
        var active = Assert.Single(handler.ActiveFiles);
        Assert.Equal("new-id", active.Id);
        Assert.Equal("overwrite-probe.txt", active.Name);
        var trashed = Assert.Single(handler.TrashedFiles);
        Assert.Equal("old-id", trashed.Id);
    }

    private static O2CloudApiClient CreateClient(IReadOnlyList<ListedFile> files)
    {
        var httpClient = new HttpClient(new FolderListingHandler(files));
        return new O2CloudApiClient(
            httpClient,
            new FixedAuthService(new O2Session { ValidationKey = "test-key" }),
            "https://cloud.o2online.es/sapi/");
    }

    private static O2CloudItemDto? InvokeFindChild(
        O2CloudApiClient api,
        string parentFolderId,
        string name,
        long expectedSize,
        string? expectedId)
    {
        var method = typeof(O2CloudApiClient).GetMethod(
            "FindChild",
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            types: [typeof(string), typeof(string), typeof(bool), typeof(long?), typeof(string)],
            modifiers: null);
        Assert.NotNull(method);

        return (O2CloudItemDto?)method.Invoke(
            api,
            [parentFolderId, name, false, (long?)expectedSize, expectedId]);
    }

    private static O2CloudItemDto? InvokeFindChildCore(
        O2CloudApiClient api,
        string parentFolderId,
        string name,
        long expectedSize,
        string expectedId,
        bool allowUniqueNameFallback)
    {
        var method = typeof(O2CloudApiClient).GetMethod(
            "FindChildCore",
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            types:
            [
                typeof(string),
                typeof(string),
                typeof(bool),
                typeof(long?),
                typeof(string),
                typeof(bool),
                typeof(CancellationToken),
            ],
            modifiers: null);
        Assert.NotNull(method);

        return (O2CloudItemDto?)method.Invoke(
            api,
            [
                parentFolderId,
                name,
                false,
                (long?)expectedSize,
                expectedId,
                allowUniqueNameFallback,
                CancellationToken.None,
            ]);
    }

    private static O2CloudItemDto Item(string id, string name, long size) => new(
        id,
        name,
        "root-test",
        IsFolder: false,
        size,
        ModifiedAt: null,
        DirectUrl: null,
        MediaKind: "file",
        Fingerprint: null,
        Node: null,
        DownloadToken: null);

    private sealed record ListedFile(string Id, string Name, string ParentId, long Size);

    private sealed class FolderListingHandler(IReadOnlyList<ListedFile> files) : HttpMessageHandler
    {
        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.RequestUri?.AbsolutePath.EndsWith("/media/folder", StringComparison.OrdinalIgnoreCase) == true
                ? "{\"data\":{\"folders\":[]}}"
                : JsonSerializer.Serialize(new
                {
                    data = new
                    {
                        media = files.Select(file => new
                        {
                            id = file.Id,
                            name = file.Name,
                            folderid = file.ParentId,
                            size = file.Size,
                        }),
                    },
                });

            return JsonResponse(body);
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(Send(request, cancellationToken));
    }

    private sealed class ReplacementTransactionHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, ListedFile> _active = new(StringComparer.OrdinalIgnoreCase)
        {
            ["old-id"] = new ListedFile("old-id", "overwrite-probe.txt", "root-test", 133),
        };
        private readonly Dictionary<string, ListedFile> _trash = new(StringComparer.OrdinalIgnoreCase);
        private bool _promotionFailureInjected;
        private int _activeListingFailuresRemaining;

        public IReadOnlyList<ListedFile> ActiveFiles => _active.Values.ToList();
        public IReadOnlyList<ListedFile> TrashedFiles => _trash.Values.ToList();
        public List<string> Mutations { get; } = [];
        public bool FailPromotion { get; init; }
        public bool FailBackupTrash { get; init; }
        public bool ReturnNonCanonicalUploadId { get; init; }

        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            var query = request.RequestUri?.Query ?? string.Empty;

            if (request.Content is MultipartFormDataContent multipart)
            {
                return HandleUpload(multipart);
            }

            if (path.EndsWith("/media/folder", StringComparison.OrdinalIgnoreCase))
            {
                return JsonResponse("{\"data\":{\"folders\":[]}}");
            }

            if (path.EndsWith("/upload/file", StringComparison.OrdinalIgnoreCase) &&
                query.Contains("action=save-metadata", StringComparison.OrdinalIgnoreCase))
            {
                return HandleRename(request);
            }

            if (path.EndsWith("/media/file", StringComparison.OrdinalIgnoreCase) &&
                query.Contains("action=delete", StringComparison.OrdinalIgnoreCase))
            {
                return HandleTrash(request);
            }

            if (path.EndsWith("/media/trash", StringComparison.OrdinalIgnoreCase) &&
                query.Contains("action=get", StringComparison.OrdinalIgnoreCase))
            {
                return JsonResponse(JsonSerializer.Serialize(new
                {
                    data = new { files = _trash.Values.Select(ToJsonFile) },
                }));
            }

            if (path.EndsWith("/media", StringComparison.OrdinalIgnoreCase) &&
                query.Contains("action=get", StringComparison.OrdinalIgnoreCase))
            {
                if (_activeListingFailuresRemaining > 0)
                {
                    _activeListingFailuresRemaining--;
                    return ErrorResponse(HttpStatusCode.ServiceUnavailable);
                }

                return JsonResponse(JsonSerializer.Serialize(new
                {
                    data = new { media = _active.Values.Select(ToJsonFile) },
                }));
            }

            return JsonResponse("{\"success\":true}");
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(Send(request, cancellationToken));

        private HttpResponseMessage HandleUpload(MultipartFormDataContent multipart)
        {
            var file = multipart.Single(part =>
                string.Equals(part.Headers.ContentDisposition?.Name?.Trim('"'), "file", StringComparison.Ordinal));
            var name = file.Headers.ContentDisposition?.FileName?.Trim('"')
                ?? throw new InvalidOperationException("La subida de prueba no incluyo nombre.");
            var size = file.Headers.ContentLength
                ?? file.ReadAsByteArrayAsync().GetAwaiter().GetResult().LongLength;
            var uploaded = new ListedFile("new-id", name, "root-test", size);
            _active[uploaded.Id] = uploaded;
            Mutations.Add($"upload:{uploaded.Id}:{uploaded.Name}");

            var responseFile = ReturnNonCanonicalUploadId
                ? uploaded with { Id = "upload-job-id" }
                : uploaded;
            return JsonResponse(JsonSerializer.Serialize(new
            {
                data = new { media = ToJsonFile(responseFile) },
            }));
        }

        private HttpResponseMessage HandleRename(HttpRequestMessage request)
        {
            using var payload = ReadFormPayload(request);
            var data = payload.RootElement.GetProperty("data");
            var id = data.GetProperty("id").GetString()
                ?? throw new InvalidOperationException("Falta el identificador en el cambio de nombre.");
            var name = data.GetProperty("name").GetString()
                ?? throw new InvalidOperationException("Falta el nombre en el cambio de nombre.");
            var parentId = data.GetProperty("folderid").GetString() ?? "root-test";
            if (FailPromotion &&
                !_promotionFailureInjected &&
                id.Equals("new-id", StringComparison.OrdinalIgnoreCase) &&
                name.Equals("overwrite-probe.txt", StringComparison.OrdinalIgnoreCase))
            {
                _promotionFailureInjected = true;
                // SendMediaListPage retries once without a JSON body for recoverable errors.
                _activeListingFailuresRemaining = 2;
                Mutations.Add($"rename-failed:{id}:{name}");
                return ErrorResponse(HttpStatusCode.ServiceUnavailable);
            }

            var current = _active[id];
            _active[id] = current with { Name = name, ParentId = parentId };
            Mutations.Add($"rename:{id}:{name}");
            return JsonResponse("{\"success\":true}");
        }

        private HttpResponseMessage HandleTrash(HttpRequestMessage request)
        {
            using var payload = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            var id = payload.RootElement
                .GetProperty("data")
                .GetProperty("files")[0]
                .GetString()
                ?? throw new InvalidOperationException("Falta el identificador en el borrado.");
            if (FailBackupTrash && id.Equals("old-id", StringComparison.OrdinalIgnoreCase))
            {
                Mutations.Add($"trash-failed:{id}");
                return ErrorResponse(HttpStatusCode.ServiceUnavailable);
            }

            var item = _active[id];
            _active.Remove(id);
            _trash[id] = item;
            Mutations.Add($"trash:{id}");
            return JsonResponse("{\"success\":true}");
        }

        private static JsonDocument ReadFormPayload(HttpRequestMessage request)
        {
            var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            var encoded = body.Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(pair => pair.Split('=', 2))
                .Single(parts => parts[0].Equals("data", StringComparison.Ordinal))[1];
            var json = Uri.UnescapeDataString(encoded.Replace('+', ' '));
            return JsonDocument.Parse(json);
        }

        private static object ToJsonFile(ListedFile file) => new
        {
            id = file.Id,
            name = file.Name,
            folderid = file.ParentId,
            size = file.Size,
            type = "file",
        };
    }

    private sealed class FixedAuthService(O2Session session) : IAuthService
    {
        public O2Session GetCurrentSession() => session;
        public bool HasStoredSession() => true;
        public bool HasValidatedSession => true;
        public O2Session? EnsureAuthenticated(bool allowInteractive, bool forceInteractive = false) => session;
        public void InvalidateSession() { }
        public bool InvalidateSessionIfCurrent(O2Session expectedSession) => false;
        public bool RefreshValidationKeyIfCurrent(O2Session expectedSession, string validationKey) => true;
        public void Logout() { }
    }

    private static HttpResponseMessage JsonResponse(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body),
    };

    private static HttpResponseMessage ErrorResponse(HttpStatusCode statusCode) => new(statusCode)
    {
        Content = new StringContent("{\"success\":false}"),
    };
}
