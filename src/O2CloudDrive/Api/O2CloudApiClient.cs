using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using O2CloudDrive.Auth;

namespace O2CloudDrive.Api;

public interface IO2CloudApiClient
{
    O2CloudItemDto GetRootFolder();
    O2StorageInfo GetStorageInfo();
    bool KeepSessionAlive();
    O2ChangeSummary GetChangesSince(long from);
    IReadOnlyList<O2CloudItemDto> ListFolder(string folderId);
    IReadOnlyList<O2CloudItemDto> ListTrash();
    byte[] DownloadFile(O2CloudItemDto item, ulong offset, uint length);
    string GetShareLink(O2CloudItemDto item);
    O2CloudItemDto CreateFolder(string parentFolderId, string name);
    O2CloudItemDto UploadFile(string parentFolderId, string name, byte[] content);
    O2CloudItemDto UploadFile(string parentFolderId, string name, Stream content, long contentLength);
    void RenameOrMove(O2CloudItemDto item, string newName, string parentFolderId);
    void MoveToTrash(O2CloudItemDto item);
    O2CloudItemDto RestoreFromTrash(O2CloudItemDto item);
    void PermanentlyDelete(O2CloudItemDto item);
}

public sealed record O2TransferProgress(
    string Phase,
    string FileName,
    long BytesTransferred,
    long TotalBytes,
    long BytesPerSecond,
    string? Message);

public sealed class O2CloudApiClient : IO2CloudApiClient
{
    private const int PageSize = 200;
    private const string PendingUploadIdPrefix = "pending:upload:";
    private const string UploadEndpoint = "https://upload.cloud.o2online.es/sapi/upload";
    private const long AsyncUploadThresholdBytes = 200L * 1024L * 1024L;
    private static readonly TimeSpan UploadStallTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan UploadResponseTimeout = TimeSpan.FromMinutes(5);
    private static readonly string[] MediaListFields =
    [
        "name",
        "modificationdate",
        "creationdate",
        "size",
        "thumbnails",
        "thumbnaildimensions",
        "viewurl",
        "videometadata",
        "audiometadata",
        "favorite",
        "shared",
        "etag",
        "origin",
        "folderid",
        "uploaded",
    ];
    private static readonly string[] MediaDetailFields =
    [
        "name",
        "url",
        "origin",
        "folderid",
        "size",
        "etag",
    ];
    private static readonly string[] MediaPlaybackFields =
    [
        "name",
        "url",
        "viewurl",
        "downloadurl",
        "playbackurl",
        "origin",
        "folderid",
        "size",
        "etag",
        "node",
        "token",
        "k",
        "videometadata",
        "audiometadata",
        "transcodingstatus",
    ];
    private static readonly string[] MediaObjectContainers = ["media", "file", "item", "metadata", "data", "properties", "content", "source"];
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly bool TraceDownloads =
        string.Equals(Environment.GetEnvironmentVariable("O2CLOUD_TRACE_DOWNLOADS"), "1", StringComparison.Ordinal);
    private readonly HttpClient _httpClient;
    private readonly IAuthService _authService;
    private readonly Uri _baseUri;

    public event EventHandler<O2TransferProgress>? TransferProgress;
    private readonly object _folderIdGate = new();
    private readonly Dictionary<string, List<string>> _folderIdCandidates = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _downloadUrlGate = new();
    private readonly Dictionary<string, string> _downloadUrlCache = new(StringComparer.OrdinalIgnoreCase);

    public O2CloudApiClient(HttpClient httpClient, IAuthService authService, string apiBaseUrl)
    {
        _httpClient = httpClient;
        _authService = authService;
        _baseUri = new Uri(apiBaseUrl, UriKind.Absolute);
    }

    public O2CloudItemDto GetRootFolder()
    {
        using var document = SendJson(HttpMethod.Post, "media/folder/root", new Dictionary<string, string?>
        {
            ["action"] = "get",
        });

        var root = document.RootElement;
        var data = ObjectProperty(root, "data");
        var folders = ArrayProperty(data, "folders");
        var folder = folders.Count > 0
            ? folders[0]
            : FirstObjectProperty(root, "rootfolder", "rootFolder", "folder") ?? root;

        var folderIdCandidates = FolderIdCandidates(folder);
        var id = folderIdCandidates.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new InvalidOperationException("O2 Cloud no devolvio la carpeta raiz.");
        }

        RegisterFolderIdCandidates(id, folderIdCandidates);
        return new O2CloudItemDto(
            id,
            FirstString(folder, "name") ?? string.Empty,
            null,
            IsFolder: true,
            Size: 0,
            ModifiedAt: FirstDate(folder, "modificationdate", "creationdate", "date"),
            DirectUrl: null,
            MediaKind: null,
            Fingerprint: null,
            Node: null,
            DownloadToken: null);
    }

    public O2StorageInfo GetStorageInfo()
    {
        using var document = SendJson(HttpMethod.Get, "media", new Dictionary<string, string?>
        {
            ["action"] = "get-storage-space",
            ["softdeleted"] = "true",
        });

        var root = document.RootElement;
        var data = FirstObjectProperty(root, "data", "storage") ?? root;
        var used = FirstLongRecursive(data, "used", "usedspace", "storageused") ?? 0;
        var free = FirstLongRecursive(data, "free", "freespace", "storagefree");
        var total = FirstLongRecursive(data, "total", "totalspace", "quota", "capacity", "storagetotal");

        if (total is null && free is not null)
        {
            total = used + free.Value;
        }

        total ??= 10L * 1024L * 1024L * 1024L * 1024L;
        var freeBytes = free ?? Math.Max(0, total.Value - used);
        freeBytes = Math.Clamp(freeBytes, 0, total.Value);
        return new O2StorageInfo(
            UsedBytes: Math.Max(0, used),
            TotalBytes: Math.Max(0, total.Value),
            FreeBytes: Math.Max(0, freeBytes));
    }

    public bool KeepSessionAlive()
    {
        try
        {
            using var _ = SendJson(HttpMethod.Get, "profile/role", new Dictionary<string, string?>
            {
                ["action"] = "get",
            });
            return true;
        }
        catch
        {
            return false;
        }
    }

    public O2ChangeSummary GetChangesSince(long from)
    {
        using var document = SendJson(HttpMethod.Get, "profile/changes", new Dictionary<string, string?>
        {
            ["action"] = "get",
            ["from"] = Math.Max(0, from).ToString(CultureInfo.InvariantCulture),
            ["locked"] = "false",
            ["origin"] = "omh,dropbox",
        });

        var root = document.RootElement;
        var data = ObjectProperty(root, "data");
        var nextCursor = FirstLong(root, "requesttime", "responsetime") ??
                         DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var folderIds = ChangeIds(data, "folder");
        var fileIds = ChangeIds(data, "file");
        var hasFileSystemChanges = folderIds.Count > 0 || fileIds.Count > 0;
        return new O2ChangeSummary(
            hasFileSystemChanges,
            nextCursor,
            folderIds,
            fileIds,
            HasNewChanges(data, "folder") || HasNewChanges(data, "file"));
    }

    public IReadOnlyList<O2CloudItemDto> ListFolder(string folderId)
    {
        Exception? lastError = null;
        List<O2CloudItemDto>? firstEmptyResult = null;

        foreach (var candidate in KnownFolderIdCandidates(folderId))
        {
            try
            {
                var items = LoadFolder(candidate);
                if (items.Count > 0)
                {
                    return SortItems(items);
                }

                firstEmptyResult ??= items;
            }
            catch (Exception ex) when (IsRecoverableFolderIdError(ex))
            {
                lastError = ex;
            }
        }

        if (firstEmptyResult is not null)
        {
            return SortItems(firstEmptyResult);
        }

        throw new InvalidOperationException("O2 Cloud no pudo listar la carpeta.", lastError);
    }

    public IReadOnlyList<O2CloudItemDto> ListTrash()
    {
        var output = new List<O2CloudItemDto>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? cursor = null;

        for (var page = 0; page < 10; page++)
        {
            try
            {
                using var document = SendJson(HttpMethod.Post, "media/trash", new Dictionary<string, string?>
                {
                    ["action"] = "get",
                }, TrashListPayload(cursor));

                var items = ParseTrashItems(document.RootElement);
                foreach (var item in items)
                {
                    var key = $"{(item.IsFolder ? "folder" : "file")}:{item.Id}";
                    if (seen.Add(key))
                    {
                        output.Add(item);
                    }
                }

                cursor = TrashNextCursor(document.RootElement);
                if (string.IsNullOrWhiteSpace(cursor) || items.Count == 0)
                {
                    break;
                }
            }
            catch (Exception ex) when (IsUnsupportedOperation(ex) || IsRecoverableFolderIdError(ex))
            {
                break;
            }
        }

        if (output.Count > 0)
        {
            return SortItems(output);
        }

        try
        {
            using var document = SendJson(HttpMethod.Post, "media/trash", new Dictionary<string, string?>
            {
                ["action"] = "get",
            });

            return SortItems(ParseTrashItems(document.RootElement));
        }
        catch (Exception ex) when (IsUnsupportedOperation(ex) || IsRecoverableFolderIdError(ex))
        {
            return [];
        }
    }

    private static object TrashListPayload(string? cursor)
    {
        var data = new Dictionary<string, object?>
        {
            ["fields"] = MediaListFields,
            ["max-page-size"] = PageSize,
        };

        if (!string.IsNullOrWhiteSpace(cursor))
        {
            data["cursor"] = cursor;
        }

        return new
        {
            data,
        };
    }

    private static string? TrashNextCursor(JsonElement root)
    {
        foreach (var container in TrashContainers(root))
        {
            var value = FirstString(container, "cursor", "nextcursor", "nextCursor", "next");
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    public byte[] DownloadFile(O2CloudItemDto item, ulong offset, uint length)
    {
        if (item.IsFolder || length == 0)
        {
            return [];
        }

        var attempted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Exception? lastError = null;

        foreach (var url in ResolveCachedReadUrls(item))
        {
            if (TryDownloadReadUrl(item, url, offset, length, attempted, out var bytes, ref lastError))
            {
                return bytes;
            }
        }

        if (IsPlayable(item))
        {
            try
            {
                var playbackUrl = ResolvePlaybackUrl(item);
                CacheDownloadUrl(item.Id, playbackUrl);
                if (TryDownloadReadUrl(item, playbackUrl, offset, length, attempted, out var bytes, ref lastError))
                {
                    return bytes;
                }
            }
            catch (Exception ex) when (IsRecoverableDownloadResolutionError(ex))
            {
                lastError = ex;
            }
        }

        try
        {
            var resolvedUrl = ResolveDownloadUrl(item);
            CacheDownloadUrl(item.Id, resolvedUrl);
            if (TryDownloadReadUrl(item, resolvedUrl, offset, length, attempted, out var bytes, ref lastError))
            {
                return bytes;
            }
        }
        catch (Exception ex) when (IsRecoverableDownloadResolutionError(ex))
        {
            lastError = ex;
        }

        foreach (var url in ResolveNativeVideoReadUrls(item))
        {
            if (TryDownloadReadUrl(item, url, offset, length, attempted, out var bytes, ref lastError))
            {
                return bytes;
            }
        }

        if (lastError is not null)
        {
            throw new IOException("O2 Cloud no pudo descargar el rango solicitado.", lastError);
        }

        return [];
    }

    public string GetShareLink(O2CloudItemDto item)
    {
        if (item.IsFolder)
        {
            var existing = TryGetExistingFolderShareLink(item);
            if (!string.IsNullOrWhiteSpace(existing))
            {
                return NormalizeShareLink(existing!);
            }

            using var folderDocument = FirstSuccess(
                () => SendFormJson(HttpMethod.Post, "link/folder", new Dictionary<string, string?>
                {
                    ["action"] = "save",
                }, new
                {
                    data = new
                    {
                        folderid = O2Id(item.Id),
                    },
                }) ?? throw new InvalidOperationException("O2 Cloud no devolvio respuesta al compartir la carpeta."),
                () => SendJson(HttpMethod.Post, "link/folder", new Dictionary<string, string?>
                {
                    ["action"] = "save",
                }, new
                {
                    data = new
                    {
                        folderid = O2Id(item.Id),
                    },
                }));

            if (folderDocument is null)
            {
                throw new InvalidOperationException("O2 Cloud no devolvio respuesta al compartir la carpeta.");
            }

            var folderShareUrl = ShareUrlFrom(folderDocument.RootElement);
            if (string.IsNullOrWhiteSpace(folderShareUrl))
            {
                throw new InvalidOperationException("O2 Cloud no devolvio el enlace compartido de la carpeta.");
            }

            return NormalizeShareLink(folderShareUrl);
        }

        var fileExisting = TryGetExistingFileShareLink(item);
        if (!string.IsNullOrWhiteSpace(fileExisting))
        {
            return NormalizeShareLink(fileExisting!);
        }

        return CreateMediaSetShareLink(item);
    }

    private string CreateMediaSetShareLink(O2CloudItemDto item)
    {
        using var document = SendJson(
            HttpMethod.Post,
            "media/set",
            new Dictionary<string, string?>
            {
                ["action"] = "save",
            },
            new
            {
                data = new
                {
                    set = new
                    {
                        items = new[] { O2Id(item.Id) },
                        description = item.Name,
                    },
                },
            });

        var shareUrl = ShareUrlFrom(document.RootElement);
        if (string.IsNullOrWhiteSpace(shareUrl))
        {
            throw new InvalidOperationException("O2 Cloud no devolvio el enlace compartido.");
        }

        return NormalizeShareLink(shareUrl);
    }

    private string? TryGetExistingFileShareLink(O2CloudItemDto item)
    {
        var singleIdPayload = new
        {
            data = new
            {
                ids = O2Id(item.Id),
            },
        };
        var arrayIdPayload = new
        {
            data = new
            {
                ids = new[] { O2Id(item.Id) },
            },
        };

        return TryReadShareLink(
            () => SendFormJson(HttpMethod.Post, "link", new Dictionary<string, string?>
            {
                ["action"] = "get",
            }, singleIdPayload),
            () => SendJson(HttpMethod.Post, "link", new Dictionary<string, string?>
            {
                ["action"] = "get",
            }, singleIdPayload),
            () => SendFormJson(HttpMethod.Post, "link", new Dictionary<string, string?>
            {
                ["action"] = "get",
            }, arrayIdPayload),
            () => SendJson(HttpMethod.Post, "link", new Dictionary<string, string?>
            {
                ["action"] = "get",
            }, arrayIdPayload));
    }

    private string? TryGetExistingFolderShareLink(O2CloudItemDto item)
    {
        var payload = new
        {
            data = new
            {
                folderids = new[] { O2Id(item.Id) },
            },
        };

        return TryReadShareLink(
            () => SendFormJson(HttpMethod.Post, "link", new Dictionary<string, string?>
            {
                ["action"] = "get",
            }, payload),
            () => SendJson(HttpMethod.Post, "link", new Dictionary<string, string?>
            {
                ["action"] = "get",
            }, payload));
    }

    private static string? TryReadShareLink(params Func<JsonDocument?>[] operations)
    {
        foreach (var operation in operations)
        {
            try
            {
                using var document = operation();
                if (document is null)
                {
                    continue;
                }

                var url = ShareUrlFrom(document.RootElement);
                if (!string.IsNullOrWhiteSpace(url))
                {
                    return url;
                }
            }
            catch
            {
            }
        }

        return null;
    }

    private IEnumerable<string> ResolveCachedReadUrls(O2CloudItemDto item)
    {
        var cachedUrl = TryGetCachedDownloadUrl(item.Id);
        if (!string.IsNullOrWhiteSpace(cachedUrl))
        {
            yield return cachedUrl!;
        }

        if (!IsPlayable(item) && !string.IsNullOrWhiteSpace(item.DirectUrl))
        {
            yield return item.DirectUrl!;
        }
    }

    private IEnumerable<string> ResolveNativeVideoReadUrls(O2CloudItemDto item)
    {
        if (IsVideo(item))
        {
            if (!string.IsNullOrWhiteSpace(item.Node) && !string.IsNullOrWhiteSpace(item.DownloadToken))
            {
                yield return BuildNativeVideoUrl(item.Name, item.Node!, item.DownloadToken!);
            }
        }
    }

    private string ResolvePlaybackUrl(O2CloudItemDto item)
    {
        var candidates = new List<string>();
        var baseServer = _baseUri.GetLeftPart(UriPartial.Authority);
        AddPlaybackCandidate(candidates, item.DirectUrl, baseServer);
        AddNativePlaybackCandidate(candidates, item, item.DownloadToken, item.Node);

        try
        {
            using var document = SendJson(
                HttpMethod.Post,
                "media",
                new Dictionary<string, string?>
                {
                    ["action"] = "get",
                    ["origin"] = "omh,dropbox",
                },
                new
                {
                    data = new
                    {
                        ids = new[] { O2Id(item.Id) },
                        fields = MediaPlaybackFields,
                    },
                });

            var root = document.RootElement;
            var data = ObjectProperty(root, "data");
            var mediaServer = FirstString(data, "mediaserverurl") ?? baseServer;
            var media = FirstArray(data, "media", "files", "videos", "audios", "pictures", "images", "items");
            if (media.Count == 0)
            {
                media = FirstArray(root, "media", "files", "videos", "audios", "pictures", "images", "items");
            }

            if (media.Count > 0)
            {
                var detail = media[0];
                var token = FirstMediaString(
                    detail,
                    "k",
                    "token",
                    "downloadtoken",
                    "downloadToken",
                    "playbacktoken",
                    "playbackToken",
                    "access_token",
                    "accessToken");
                var node = FirstMediaString(
                    detail,
                    "node",
                    "servernode",
                    "serverNode",
                    "storageNode",
                    "storagenode");

                var playbackUrl = AbsoluteMediaUrl(FirstMediaString(detail, "playbackurl"), mediaServer);
                var url = AbsoluteMediaUrl(FirstMediaString(detail, "url"), mediaServer);
                var viewUrl = AbsoluteMediaUrl(FirstMediaString(detail, "viewurl"), mediaServer);
                var downloadUrl = AbsoluteMediaUrl(FirstMediaString(detail, "downloadurl"), mediaServer);

                token ??= FirstQueryValue(playbackUrl, "k", "token");
                token ??= FirstQueryValue(url, "k", "token");
                token ??= FirstQueryValue(viewUrl, "k", "token");
                token ??= FirstQueryValue(downloadUrl, "k", "token");
                node ??= FirstQueryValue(playbackUrl, "node");
                node ??= FirstQueryValue(url, "node");
                node ??= FirstQueryValue(viewUrl, "node");
                node ??= FirstQueryValue(downloadUrl, "node");

                AddNativePlaybackCandidate(candidates, item, token, node);
                AddPlaybackCandidate(candidates, playbackUrl, mediaServer);
                AddPlaybackCandidate(candidates, url, mediaServer);
                AddPlaybackCandidate(candidates, viewUrl, mediaServer);
                AddPlaybackCandidate(candidates, downloadUrl, mediaServer);
            }
        }
        catch (Exception ex) when (IsRecoverableDownloadResolutionError(ex))
        {
            // The generic download URL fallback below still works for non-playback endpoints.
        }

        foreach (var candidate in candidates)
        {
            if (ProbeRangeStatus(candidate, item.Name) == HttpStatusCode.PartialContent)
            {
                return candidate;
            }
        }

        var fallback = candidates.FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(fallback))
        {
            return fallback!;
        }

        throw new InvalidOperationException($"O2 Cloud no devolvio URL de reproduccion para {item.Name}.");
    }

    private void AddNativePlaybackCandidate(ICollection<string> candidates, O2CloudItemDto item, string? token, string? node)
    {
        if (IsVideo(item) && !string.IsNullOrWhiteSpace(token) && !string.IsNullOrWhiteSpace(node))
        {
            AddPlaybackCandidate(candidates, BuildNativeVideoUrl(item.Name, node!, token!), _baseUri.GetLeftPart(UriPartial.Authority));
        }
    }

    private static void AddPlaybackCandidate(ICollection<string> candidates, string? rawUrl, string mediaServer)
    {
        var url = AbsoluteMediaUrl(rawUrl, mediaServer).Trim();
        if (string.IsNullOrWhiteSpace(url) ||
            candidates.Contains(url, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        candidates.Add(url);
    }

    private bool TryDownloadReadUrl(
        O2CloudItemDto item,
        string url,
        ulong offset,
        uint length,
        ISet<string> attempted,
        out byte[] bytes,
        ref Exception? lastError)
    {
        bytes = [];
        if (string.IsNullOrWhiteSpace(url) || !attempted.Add(url))
        {
            return false;
        }

        try
        {
            bytes = DownloadRange(url, item.Name, offset, length);
            return bytes.Length > 0 || offset >= (ulong)Math.Max(0, item.Size);
        }
        catch (HttpRequestException ex) when (IsRecoverableDownloadUrlError(ex))
        {
            RemoveCachedDownloadUrl(item.Id, url);
            lastError = ex;
            return false;
        }
    }

    private byte[] DownloadRange(string rawUrl, string fileName, ulong offset, uint length)
    {
        var uri = NormalizeDownloadUri(rawUrl, fileName);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        ApplySessionHeaders(request);
        request.Headers.TryAddWithoutValidation("Accept", "*/*");
        request.Headers.Range = new RangeHeaderValue((long)offset, checked((long)(offset + length - 1)));

        using var response = _httpClient.Send(request, HttpCompletionOption.ResponseHeadersRead);
        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            TraceDownload(uri, response.StatusCode, offset, length, 0);
            return [];
        }

        response.EnsureSuccessStatusCode();
        if (response.StatusCode != HttpStatusCode.PartialContent && offset > 0)
        {
            TraceDownload(uri, response.StatusCode, offset, length, 0);
            throw new HttpRequestException("La URL de descarga no soporta Range.", null, response.StatusCode);
        }

        using var stream = response.Content.ReadAsStream();
        var bytes = ReadAtMost(stream, length);
        TraceDownload(uri, response.StatusCode, offset, length, bytes.Length);
        return bytes;
    }

    private HttpStatusCode? ProbeRangeStatus(string rawUrl, string fileName)
    {
        try
        {
            var uri = NormalizeDownloadUri(rawUrl, fileName);
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            ApplySessionHeaders(request);
            request.Headers.TryAddWithoutValidation("Accept", "*/*");
            request.Headers.Range = new RangeHeaderValue(0, 1023);

            using var response = _httpClient.Send(request, HttpCompletionOption.ResponseHeadersRead);
            TraceDownload(uri, response.StatusCode, 0, 1024, 0);
            return response.StatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or UriFormatException or InvalidOperationException)
        {
            return null;
        }
    }

    private static byte[] ReadAtMost(Stream stream, uint length)
    {
        var requested = checked((int)Math.Min(length, int.MaxValue));
        var buffer = new byte[requested];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = stream.Read(buffer, total, buffer.Length - total);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        if (total == buffer.Length)
        {
            return buffer;
        }

        var result = new byte[total];
        Buffer.BlockCopy(buffer, 0, result, 0, total);
        return result;
    }

    private static void TraceDownload(Uri uri, HttpStatusCode statusCode, ulong offset, uint length, int bytes)
    {
        if (!TraceDownloads)
        {
            return;
        }

        Console.Error.WriteLine(
            $"download {uri.Host}{uri.AbsolutePath} status={(int)statusCode} offset={offset} length={length} bytes={bytes}");
    }

    public O2CloudItemDto CreateFolder(string parentFolderId, string name)
    {
        var simplePayload = new
        {
            data = new
            {
                name,
                parentid = O2Id(parentFolderId),
            },
        };
        var fullPayload = new
        {
            data = new
            {
                magic = false,
                offline = false,
                name,
                parentid = O2Id(parentFolderId),
            },
        };

        using var document = FirstSuccess(
            () => SendJson(HttpMethod.Post, "media/folder", new Dictionary<string, string?>
            {
                ["action"] = "save",
            }, simplePayload),
            () => SendJson(HttpMethod.Post, "media/folder", new Dictionary<string, string?>
            {
                ["action"] = "save",
            }, fullPayload),
            () => SendFormJson(HttpMethod.Post, "media/folder", new Dictionary<string, string?>
            {
                ["action"] = "save",
            }, simplePayload),
            () => SendFormJson(HttpMethod.Post, "media/folder", new Dictionary<string, string?>
            {
                ["action"] = "save",
            }, fullPayload));

        var created = TryParseItem(document, parentFolderId, expectedIsFolder: true, fallbackName: name)
            ?? FindChild(parentFolderId, name, isFolder: true);

        return created ?? throw new InvalidOperationException("O2 Cloud creo la carpeta, pero no devolvio su identificador.");
    }

    public O2CloudItemDto UploadFile(string parentFolderId, string name, byte[] content)
    {
        using var stream = new MemoryStream(content, writable: false);
        return UploadFile(parentFolderId, name, stream, content.LongLength);
    }

    public O2CloudItemDto UploadFile(string parentFolderId, string name, Stream content, long contentLength)
    {
        try
        {
            return UploadFileCore(parentFolderId, name, content, contentLength);
        }
        catch (Exception ex)
        {
            ReportTransfer("failed", name, 0, contentLength, message: UploadFailureMessage(ex));
            throw;
        }
    }

    private O2CloudItemDto UploadFileCore(string parentFolderId, string name, Stream content, long contentLength)
    {
        var metadata = new Dictionary<string, object?>
        {
            ["name"] = name,
            ["size"] = contentLength,
            ["modificationdate"] = string.Empty,
            ["folderid"] = O2Id(parentFolderId),
        };

        var contentType = ContentTypeFor(name);
        if (!string.IsNullOrWhiteSpace(contentType))
        {
            metadata["contenttype"] = contentType;
        }

        var payload = new Dictionary<string, object?>
        {
            ["data"] = metadata,
        };

        var useAsyncUpload = contentLength > AsyncUploadThresholdBytes;
        using var request = new HttpRequestMessage(HttpMethod.Post, BuildUploadUri(useAsyncUpload));
        ApplySessionHeaders(request);
        request.Headers.TryAddWithoutValidation("X-deviceid", "O2CloudDrive");
        request.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
        request.Headers.TryAddWithoutValidation("Origin", _baseUri.GetLeftPart(UriPartial.Authority));
        request.Headers.TryAddWithoutValidation("Referer", _baseUri.GetLeftPart(UriPartial.Authority) + "/");
        request.Headers.TryAddWithoutValidation("Accept", "*/*");
        request.Headers.TryAddWithoutValidation("Connection", "keep-alive");

        var multipart = new MultipartFormDataContent();
        multipart.Add(new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8), "data");
        if (content.CanSeek)
        {
            content.Position = 0;
        }

        using var uploadCancellation = new CancellationTokenSource();
        using var stallTimer = new System.Threading.Timer(
            _ => uploadCancellation.Cancel(),
            null,
            UploadStallTimeout,
            Timeout.InfiniteTimeSpan);
        long lastUploadProgressBytes = 0;

        ReportTransfer("start", name, 0, contentLength);
        var fileContent = new ProgressStreamContent(
            content,
            contentLength,
            (sent, total, bytesPerSecond) =>
            {
                if (sent == 0 || sent > lastUploadProgressBytes)
                {
                    lastUploadProgressBytes = sent;
                    var timeout = total > 0 && sent >= total
                        ? UploadResponseTimeout
                        : UploadStallTimeout;
                    stallTimer.Change(timeout, Timeout.InfiniteTimeSpan);
                }

                ReportTransfer("progress", name, sent, total, bytesPerSecond);
            });
        fileContent.Headers.ContentLength = contentLength;
        if (!string.IsNullOrWhiteSpace(contentType))
        {
            fileContent.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        }

        multipart.Add(fileContent, "file", name);
        request.Content = multipart;

        using var response = SendUploadRequest(request, uploadCancellation.Token);
        stallTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        var text = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new UnauthorizedAccessException("O2 Cloud rechazo la subida porque la sesion no esta autorizada.");
        }

        if (useAsyncUpload &&
            response.StatusCode is HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout)
        {
            ReportTransfer("confirming", name, contentLength, contentLength);
            var confirmedAfterTimeout = FindChildWithRetries(
                parentFolderId,
                name,
                isFolder: false,
                expectedSize: contentLength,
                onRetry: seconds => ReportTransfer("waiting", name, seconds, contentLength));
            if (confirmedAfterTimeout is not null)
            {
                ReportTransfer("completed", name, contentLength, contentLength);
                return confirmedAfterTimeout;
            }

            throw new IOException(
                "O2 Cloud ha recibido la subida en modo asincrono, pero el archivo todavia no aparece confirmado en la carpeta remota.");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new IOException($"O2 Cloud rechazo la subida HTTP {(int)response.StatusCode}: {TrimPreview(text)}");
        }

        ReportTransfer("confirming", name, contentLength, contentLength);
        JsonDocument? document;
        try
        {
            document = ParseOptionalJson(text);
        }
        catch (JsonException ex)
        {
            if (IsUploadAccepted(null, text))
            {
                var listedAfterAcceptedText = FindChildWithRetries(
                    parentFolderId,
                    name,
                    isFolder: false,
                    expectedSize: contentLength,
                    onRetry: seconds => ReportTransfer("waiting", name, seconds, contentLength));
                if (listedAfterAcceptedText is not null)
                {
                    ReportTransfer("completed", name, contentLength, contentLength);
                    return listedAfterAcceptedText;
                }

                throw new IOException("O2 Cloud acepto la subida, pero el archivo no aparece confirmado en el listado remoto.");
            }

            throw new IOException($"O2 Cloud devolvio una respuesta de subida no reconocida: {TrimPreview(text)}", ex);
        }

        using (document)
        {
            var uploaded = TryParseItem(document, parentFolderId, expectedIsFolder: false, fallbackName: name);
            if (uploaded is not null)
            {
                var confirmed = FindChildWithRetries(
                    parentFolderId,
                    name,
                    isFolder: false,
                    expectedSize: contentLength,
                    expectedId: uploaded.Id,
                    onRetry: seconds => ReportTransfer("waiting", name, seconds, contentLength));
                confirmed ??= FindChildWithRetries(
                    parentFolderId,
                    name,
                    isFolder: false,
                    expectedSize: contentLength,
                    onRetry: seconds => ReportTransfer("waiting", name, seconds, contentLength));
                if (confirmed is not null)
                {
                    ReportTransfer("completed", name, contentLength, contentLength);
                    return confirmed;
                }

                ReportTransfer("completed", name, contentLength, contentLength);
                return uploaded;
            }

            O2CloudItemDto? listed = null;
            Exception? listingException = null;
            try
            {
                listed = FindChildWithRetries(
                    parentFolderId,
                    name,
                    isFolder: false,
                    expectedSize: contentLength,
                    onRetry: seconds => ReportTransfer("waiting", name, seconds, contentLength));
            }
            catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or IOException)
            {
                listingException = ex;
            }

            if (listed is not null)
            {
                ReportTransfer("completed", name, contentLength, contentLength);
                return listed;
            }

            if (IsUploadAccepted(document, text))
            {
                throw new IOException("O2 Cloud acepto la subida, pero el archivo no aparece confirmado en el listado remoto.");
            }

            throw listingException is null
                ? new InvalidOperationException("O2 Cloud recibio la subida, pero no devolvio confirmacion ni el archivo aparece al refrescar la carpeta.")
                : new IOException("O2 Cloud recibio la subida, pero fallo al comprobar el listado de la carpeta.", listingException);
        }
    }

    public void RenameOrMove(O2CloudItemDto item, string newName, string parentFolderId)
    {
        if (item.IsFolder)
        {
            var payload = new
            {
                data = new
                {
                    magic = false,
                    offline = false,
                    id = O2Id(item.Id),
                    name = newName,
                    parentid = O2Id(parentFolderId),
                },
            };

            FirstSuccess(
                () => Dispose(SendFormJson(HttpMethod.Post, "media/folder", new Dictionary<string, string?>
                {
                    ["action"] = "save",
                }, payload)),
                () => Dispose(SendJson(HttpMethod.Post, "media/folder", new Dictionary<string, string?>
                {
                    ["action"] = "save",
                }, payload)));
            return;
        }

        var saveMetadataPayload = new
        {
            data = new
            {
                id = O2Id(item.Id),
                name = newName,
                folderid = O2Id(parentFolderId),
            },
        };
        var mediaKind = NormalizeMediaKind(item.MediaKind ?? MediaKindFor(item.Name, null));
        var operations = new List<Action>
        {
            () => Dispose(SendFormJson(HttpMethod.Post, $"upload/{mediaKind}", new Dictionary<string, string?>
            {
                ["action"] = "save-metadata",
            }, saveMetadataPayload)),
        };

        if (!mediaKind.Equals("file", StringComparison.OrdinalIgnoreCase))
        {
            operations.Add(() => Dispose(SendFormJson(HttpMethod.Post, "upload/file", new Dictionary<string, string?>
            {
                ["action"] = "save-metadata",
            }, saveMetadataPayload)));
        }

        if (string.Equals(item.Name, newName, StringComparison.OrdinalIgnoreCase))
        {
            var movePayload = new
            {
                data = new
                {
                    ids = new[] { O2Id(item.Id) },
                    parentid = O2Id(parentFolderId),
                },
            };

            operations.Add(() => Dispose(SendJson(HttpMethod.Post, "media/file", new Dictionary<string, string?>
            {
                ["action"] = "move",
            }, movePayload)));
        }

        FirstSuccess(operations.ToArray());
    }

    public void MoveToTrash(O2CloudItemDto item)
    {
        if (IsPendingUploadId(item.Id))
        {
            item = ResolvePendingUploadForMutation(item);
        }

        if (item.IsFolder)
        {
            var payload = new
            {
                data = new
                {
                    ids = new[] { O2Id(item.Id) },
                },
            };

            FirstSuccess(
                () => Dispose(SendJson(HttpMethod.Post, "media/folder", new Dictionary<string, string?>
                {
                    ["action"] = "softdelete",
                }, payload)),
                () => Dispose(SendFormJson(HttpMethod.Post, "media/folder", new Dictionary<string, string?>
                {
                    ["action"] = "softdelete",
                }, payload)));
            VerifyMovedToTrash(item);
            return;
        }

        var mediaKind = NormalizeMediaKind(item.MediaKind ?? MediaKindFor(item.Name, null));
        var operations = new List<Action>();
        foreach (var bodyName in DeletePayloadNamesFor(mediaKind, softDelete: true))
        {
            var payload = new
            {
                data = new Dictionary<string, object?>
                {
                    [bodyName] = new[] { O2Id(item.Id) },
                },
            };

            operations.Add(() => Dispose(SendJson(HttpMethod.Post, $"media/{mediaKind}", new Dictionary<string, string?>
            {
                ["action"] = "delete",
                ["softdelete"] = "true",
            }, payload)));
        }

        var fileFallbackPayload = new
        {
            data = new
            {
                files = new[] { O2Id(item.Id) },
            },
        };
        operations.Add(() => Dispose(SendJson(HttpMethod.Post, "media/file", new Dictionary<string, string?>
        {
            ["action"] = "delete",
            ["softdelete"] = "true",
        }, fileFallbackPayload)));

        FirstSuccess(operations.ToArray());
        VerifyMovedToTrash(item);
    }

    public O2CloudItemDto RestoreFromTrash(O2CloudItemDto item)
    {
        if (IsPendingUploadId(item.Id))
        {
            item = ResolvePendingUploadForMutation(item);
        }

        var oneIdPayload = new
        {
            data = new
            {
                id = O2Id(item.Id),
            },
        };
        var idsPayload = new
        {
            data = new
            {
                ids = new[] { O2Id(item.Id) },
            },
        };

        using var document = item.IsFolder
            ? FirstSuccess(
                () => SendJson(HttpMethod.Post, "trash/folder", new Dictionary<string, string?>
                {
                    ["action"] = "restore",
                }, oneIdPayload),
                () => SendFormJson(HttpMethod.Post, "trash/folder", new Dictionary<string, string?>
                {
                    ["action"] = "restore",
                }, oneIdPayload) ?? throw new InvalidOperationException("O2 Cloud no devolvio respuesta al restaurar la carpeta."),
                () => SendJson(HttpMethod.Post, "trash/folder", new Dictionary<string, string?>
                {
                    ["action"] = "restore",
                }, idsPayload))
            : FirstSuccess(
                () => SendJson(HttpMethod.Post, "trash/file", new Dictionary<string, string?>
                {
                    ["action"] = "restore",
                }, oneIdPayload),
                () => SendFormJson(HttpMethod.Post, "trash/file", new Dictionary<string, string?>
                {
                    ["action"] = "restore",
                }, oneIdPayload) ?? throw new InvalidOperationException("O2 Cloud no devolvio respuesta al restaurar el archivo."),
                () => SendJson(HttpMethod.Post, "media/trash", new Dictionary<string, string?>
                {
                    ["action"] = "restore",
                }, oneIdPayload),
                () => SendJson(HttpMethod.Post, "media/trash", new Dictionary<string, string?>
                {
                    ["action"] = "restore",
                }, idsPayload));

        return TryParseItem(document, item.ParentId ?? string.Empty, item.IsFolder, item.Name)
            ?? new O2CloudItemDto(
                item.Id,
                FirstString(ObjectProperty(document.RootElement, "data"), "name") ??
                FirstString(document.RootElement, "name") ??
                item.Name,
                item.ParentId,
                item.IsFolder,
                item.Size,
                item.ModifiedAt,
                item.DirectUrl,
                item.MediaKind,
                item.Fingerprint,
                item.Node,
                item.DownloadToken);
    }

    public void PermanentlyDelete(O2CloudItemDto item)
    {
        if (IsPendingUploadId(item.Id))
        {
            item = ResolvePendingUploadForMutation(item);
        }

        if (item.IsFolder)
        {
            var oneIdPayload = new
            {
                data = new
                {
                    id = O2Id(item.Id),
                },
            };
            var folderIdsPayload = new
            {
                data = new
                {
                    ids = new[] { O2Id(item.Id) },
                },
            };
            var foldersPayload = new
            {
                data = new
                {
                    folders = new[] { O2Id(item.Id) },
                },
            };

            FirstSuccess(
                () => Dispose(SendJson(HttpMethod.Post, "media/folder", new Dictionary<string, string?>
                {
                    ["action"] = "delete",
                    ["softdelete"] = "false",
                }, foldersPayload)),
                () => Dispose(SendJson(HttpMethod.Post, "trash/folder", new Dictionary<string, string?>
                {
                    ["action"] = "delete",
                }, foldersPayload)),
                () => Dispose(SendJson(HttpMethod.Post, "trash/folder", new Dictionary<string, string?>
                {
                    ["action"] = "delete",
                }, oneIdPayload)),
                () => Dispose(SendJson(HttpMethod.Post, "trash/folder", new Dictionary<string, string?>
                {
                    ["action"] = "delete",
                }, folderIdsPayload)),
                () => Dispose(SendJson(HttpMethod.Post, "media/trash", new Dictionary<string, string?>
                {
                    ["action"] = "delete",
                }, foldersPayload)),
                () => Dispose(SendFormJson(HttpMethod.Post, "media/trash", new Dictionary<string, string?>
                {
                    ["action"] = "delete",
                }, foldersPayload)),
                () => Dispose(SendJson(HttpMethod.Post, "media/folder", new Dictionary<string, string?>
                {
                    ["action"] = "delete",
                }, foldersPayload)));
            return;
        }

        var mediaKind = NormalizeMediaKind(item.MediaKind ?? MediaKindFor(item.Name, null));
        var idsPayload = new
        {
            data = new
            {
                ids = new[] { O2Id(item.Id) },
            },
        };
        var operations = new List<Action>
        {
            () => Dispose(SendJson(HttpMethod.Post, "media", new Dictionary<string, string?>
            {
                ["action"] = "delete",
            }, idsPayload)),
            () => Dispose(SendJson(HttpMethod.Post, "media/trash", new Dictionary<string, string?>
            {
                ["action"] = "delete",
            }, idsPayload)),
            () => Dispose(SendFormJson(HttpMethod.Post, "media/trash", new Dictionary<string, string?>
            {
                ["action"] = "delete",
            }, idsPayload)),
        };

        foreach (var bodyName in DeletePayloadNamesFor(mediaKind, softDelete: false))
        {
            var typedPayload = new
            {
                data = new Dictionary<string, object?>
                {
                    [bodyName] = new[] { O2Id(item.Id) },
                },
            };
            operations.Add(() => Dispose(SendJson(HttpMethod.Post, "media/trash", new Dictionary<string, string?>
            {
                ["action"] = "delete",
            }, typedPayload)));
            operations.Add(() => Dispose(SendFormJson(HttpMethod.Post, "media/trash", new Dictionary<string, string?>
            {
                ["action"] = "delete",
            }, typedPayload)));
            operations.Add(() => Dispose(SendJson(HttpMethod.Post, $"media/{mediaKind}", new Dictionary<string, string?>
            {
                ["action"] = "delete",
            }, typedPayload)));
        }

        var fileFallbackPayload = new
        {
            data = new
            {
                files = new[] { O2Id(item.Id) },
            },
        };
        operations.Add(() => Dispose(SendJson(HttpMethod.Post, "media/file", new Dictionary<string, string?>
        {
            ["action"] = "delete",
        }, fileFallbackPayload)));

        FirstSuccess(operations.ToArray());
    }

    private void LoadFolders(string folderId, ICollection<O2CloudItemDto> items)
    {
        var currentFolderIds = KnownFolderIdCandidates(folderId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pageSignatures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var offset = 0; ; offset += PageSize)
        {
            using var document = SendJson(HttpMethod.Get, "media/folder", new Dictionary<string, string?>
            {
                ["action"] = "list",
                ["parentid"] = folderId,
                ["limit"] = PageSize.ToString(CultureInfo.InvariantCulture),
                ["offset"] = offset > 0 ? offset.ToString(CultureInfo.InvariantCulture) : null,
            });

            var root = document.RootElement;
            var data = ObjectProperty(root, "data");
            var folders = ArrayProperty(data, "folders");
            if (folders.Count == 0)
            {
                folders = ArrayProperty(root, "folders");
            }

            if (!pageSignatures.Add(PageSignature(folders, FolderIdCandidates)))
            {
                break;
            }

            var addedInPage = 0;
            foreach (var folder in folders)
            {
                var folderIdCandidates = FolderIdCandidates(folder);
                var id = folderIdCandidates
                    .FirstOrDefault(candidate => !currentFolderIds.Contains(candidate));
                if (string.IsNullOrWhiteSpace(id))
                {
                    continue;
                }

                if (!seen.Add(id))
                {
                    continue;
                }

                RegisterFolderIdCandidates(id, folderIdCandidates);
                items.Add(new O2CloudItemDto(
                    id,
                    FirstString(folder, "name") ?? "Carpeta",
                    folderId,
                    IsFolder: true,
                    Size: 0,
                    ModifiedAt: FirstDate(folder, "modificationdate", "creationdate", "date"),
                    DirectUrl: null,
                    MediaKind: null,
                    Fingerprint: null,
                    Node: null,
                    DownloadToken: null));
                addedInPage++;
            }

            if (folders.Count == 0 || addedInPage == 0 || folders.Count < PageSize)
            {
                break;
            }
        }
    }

    private List<O2CloudItemDto> LoadFolder(string folderId)
    {
        var items = new List<O2CloudItemDto>();
        Exception? firstError = null;

        try
        {
            LoadFolders(folderId, items);
        }
        catch (Exception ex) when (IsRecoverableFolderIdError(ex))
        {
            firstError = ex;
        }

        try
        {
            LoadFiles(folderId, items);
        }
        catch (Exception ex) when (IsRecoverableFolderIdError(ex))
        {
            firstError ??= ex;
        }

        if (items.Count == 0 && firstError is not null)
        {
            throw firstError;
        }

        return items;
    }

    private static IReadOnlyList<O2CloudItemDto> SortItems(IEnumerable<O2CloudItemDto> items)
    {
        return items
            .OrderBy(item => item.IsFolder ? 0 : 1)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private IReadOnlyList<string> KnownFolderIdCandidates(string folderId)
    {
        lock (_folderIdGate)
        {
            return _folderIdCandidates.TryGetValue(folderId, out var candidates)
                ? candidates.ToList()
                : [folderId];
        }
    }

    private void RegisterFolderIdCandidates(string primaryId, IReadOnlyList<string> candidates)
    {
        var ordered = candidates
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Prepend(primaryId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (ordered.Count == 0)
        {
            return;
        }

        lock (_folderIdGate)
        {
            foreach (var candidate in ordered)
            {
                _folderIdCandidates[candidate] = ordered;
            }
        }
    }

    private string? TryGetCachedDownloadUrl(string itemId)
    {
        if (string.IsNullOrWhiteSpace(itemId))
        {
            return null;
        }

        lock (_downloadUrlGate)
        {
            return _downloadUrlCache.TryGetValue(itemId, out var url) ? url : null;
        }
    }

    private void CacheDownloadUrl(string itemId, string url)
    {
        if (string.IsNullOrWhiteSpace(itemId) || string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        lock (_downloadUrlGate)
        {
            _downloadUrlCache[itemId] = url;
        }
    }

    private void RemoveCachedDownloadUrl(string itemId, string url)
    {
        if (string.IsNullOrWhiteSpace(itemId) || string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        lock (_downloadUrlGate)
        {
            if (_downloadUrlCache.TryGetValue(itemId, out var cached) &&
                string.Equals(cached, url, StringComparison.OrdinalIgnoreCase))
            {
                _downloadUrlCache.Remove(itemId);
            }
        }
    }

    private void LoadFiles(string folderId, ICollection<O2CloudItemDto> items)
    {
        var currentFolderIds = KnownFolderIdCandidates(folderId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pageSignatures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var offset = 0; ; offset += PageSize)
        {
            using var document = SendMediaListPage(folderId, offset);

            var root = document.RootElement;
            var data = ObjectProperty(root, "data");
            var mediaServer = FirstString(data, "mediaserverurl") ?? _baseUri.GetLeftPart(UriPartial.Authority);
            var files = FirstArray(data, "media", "files", "videos", "audios", "pictures", "images", "items");
            if (files.Count == 0)
            {
                files = FirstArray(root, "media", "files", "videos", "audios", "pictures", "images", "items");
            }

            if (!pageSignatures.Add(PageSignature(files, MediaIdCandidates)))
            {
                break;
            }

            var addedInPage = 0;
            foreach (var file in files)
            {
                if (LooksLikeFolder(file))
                {
                    continue;
                }

                var id = MediaIdCandidates(file).FirstOrDefault();
                var name = FirstMediaString(file, "name", "filename", "title");
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                var size = FirstMediaLong(file, "size", "filesize", "fileSize", "contentlength", "contentLength")
                    ?? 0;
                var duplicateKey = !string.IsNullOrWhiteSpace(id)
                    ? $"id:{id}"
                    : $"name:{name}|size:{size}|folder:{folderId}";
                if (!seen.Add(duplicateKey))
                {
                    continue;
                }

                var returnedFolder = FirstMediaString(file, "folder", "folderid", "folderId", "parentid")
                    ?? folderId;
                if (!string.IsNullOrWhiteSpace(returnedFolder) &&
                    !currentFolderIds.Contains(returnedFolder))
                {
                    continue;
                }

                var rawType = FirstMediaString(file, "type", "mediatype", "mimetype", "contenttype");
                var mediaKind = MediaKindFor(name, rawType);
                var node = FirstMediaString(file, "node", "servernode", "serverNode", "storageNode", "storagenode");
                var token = FirstMediaString(file, "k", "token", "downloadtoken", "downloadToken", "playbacktoken", "playbackToken");
                var playbackUrl = AbsoluteMediaUrl(
                    FirstMediaString(file, "playbackurl", "viewurl"),
                    mediaServer);
                token ??= FirstQueryValue(playbackUrl, "k", "token");
                node ??= FirstQueryValue(playbackUrl, "node");
                var rawUrl = AbsoluteMediaUrl(
                    FirstMediaString(file, "url", "downloadurl"),
                    mediaServer);
                items.Add(new O2CloudItemDto(
                    string.IsNullOrWhiteSpace(id) ? duplicateKey : id,
                    name,
                    folderId,
                    IsFolder: false,
                    Size: Math.Max(0, size),
                    ModifiedAt: FirstMediaDate(file, "modificationdate", "creationdate", "uploaded", "date"),
                    DirectUrl: rawUrl,
                    MediaKind: mediaKind,
                    Fingerprint: FirstMediaString(file, "fingerprint", "hash", "etag", "sha1", "checksum"),
                    Node: node,
                    DownloadToken: token));
                addedInPage++;
            }

            var hasMore = BoolProperty(data, "more");
            if (files.Count == 0 || addedInPage == 0 || (!hasMore && files.Count < PageSize))
            {
                break;
            }
        }
    }

    private JsonDocument SendMediaListPage(string folderId, int offset)
    {
        var query = new Dictionary<string, string?>
        {
            ["action"] = "get",
            ["folderid"] = folderId,
            ["limit"] = PageSize.ToString(CultureInfo.InvariantCulture),
            ["offset"] = offset > 0 ? offset.ToString(CultureInfo.InvariantCulture) : null,
        };

        try
        {
            return SendJson(HttpMethod.Post, "media", query, new
            {
                data = new
                {
                    fields = MediaListFields,
                },
            });
        }
        catch (Exception ex) when (IsRecoverableFolderIdError(ex))
        {
            return SendJson(HttpMethod.Post, "media", query);
        }
    }

    private IReadOnlyDictionary<string, JsonElement> LoadMediaDetails(IEnumerable<string> rawIds)
    {
        var ids = rawIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(PageSize)
            .ToArray();
        if (ids.Length == 0)
        {
            return new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            using var document = SendJson(
                HttpMethod.Post,
                "media",
                new Dictionary<string, string?>
                {
                    ["action"] = "get",
                    ["origin"] = "omh,dropbox",
                },
                new
                {
                    data = new
                    {
                        ids = ids.Select(O2Id).ToArray(),
                        fields = MediaDetailFields,
                    },
                });

            var root = document.RootElement;
            var data = ObjectProperty(root, "data");
            var media = FirstArray(data, "media", "files", "videos", "audios", "pictures", "images", "items");
            if (media.Count == 0)
            {
                media = FirstArray(root, "media", "files", "videos", "audios", "pictures", "images", "items");
            }

            var details = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in media)
            {
                foreach (var id in MediaIdCandidates(item))
                {
                    details.TryAdd(id, item.Clone());
                }
            }

            return details;
        }
        catch (Exception ex) when (IsRecoverableFolderIdError(ex))
        {
            return new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private string ResolveDownloadUrl(O2CloudItemDto item)
    {
        using var document = SendJson(
            HttpMethod.Post,
            "media",
            new Dictionary<string, string?>
            {
                ["action"] = "get",
                ["origin"] = "omh,dropbox",
            },
            new
            {
                data = new
                {
                    ids = new[] { item.Id },
                    fields = MediaDetailFields,
                },
            });

        var root = document.RootElement;
        var data = ObjectProperty(root, "data");
        var media = FirstArray(data, "media", "files");
        if (media.Count == 0)
        {
            media = FirstArray(root, "media", "files");
        }

        var url = media.Count == 0 ? null : FirstMediaString(media[0], "url", "downloadurl");
        if (string.IsNullOrWhiteSpace(url))
        {
            throw new InvalidOperationException($"O2 Cloud no devolvio URL temporal para {item.Name}.");
        }

        return url!;
    }

    private List<O2CloudItemDto> ParseTrashItems(JsonElement root)
    {
        var output = new List<O2CloudItemDto>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var containers = TrashContainers(root);

        foreach (var item in TrashArrayItems(containers, "folders", "folder", "trashfolders", "deletedfolders", "directories"))
        {
            AddTrashItem(output, seen, item, folderHint: true);
        }

        foreach (var item in TrashArrayItems(containers, "files", "file", "trashfiles", "deletedfiles", "media"))
        {
            AddTrashItem(output, seen, item, folderHint: false);
        }

        foreach (var item in TrashArrayItems(containers, "entries", "items", "deleted", "trash", "children", "objects"))
        {
            AddTrashItem(output, seen, item, TrashFolderHint(item));
        }

        return output;
    }

    private static IReadOnlyList<JsonElement> TrashContainers(JsonElement root)
    {
        var containers = new List<JsonElement>();
        AddTrashContainer(containers, root);
        AddTrashContainer(containers, ObjectProperty(root, "data"));

        foreach (var container in containers.ToArray())
        {
            AddTrashContainer(containers, ObjectProperty(container, "trash"));
            AddTrashContainer(containers, ObjectProperty(container, "deleted"));
            AddTrashContainer(containers, ObjectProperty(container, "result"));
        }

        return containers;
    }

    private static void AddTrashContainer(ICollection<JsonElement> containers, JsonElement container)
    {
        if (container.ValueKind == JsonValueKind.Object)
        {
            containers.Add(container);
        }
    }

    private static IEnumerable<JsonElement> TrashArrayItems(IEnumerable<JsonElement> containers, params string[] arrayNames)
    {
        foreach (var container in containers)
        {
            foreach (var arrayName in arrayNames)
            {
                foreach (var item in ArrayProperty(container, arrayName))
                {
                    if (item.ValueKind == JsonValueKind.Object)
                    {
                        yield return item;
                    }
                }
            }
        }
    }

    private static bool? TrashFolderHint(JsonElement item)
    {
        var rawType = FirstMediaString(item, "type", "mediatype", "mediaType", "mimetype", "contenttype", "kind");
        var type = rawType?.ToLowerInvariant() ?? string.Empty;
        if (type.Contains("folder", StringComparison.Ordinal) ||
            type.Contains("directory", StringComparison.Ordinal))
        {
            return true;
        }

        if (type.Contains("file", StringComparison.Ordinal) ||
            type.Contains("media", StringComparison.Ordinal) ||
            type.Contains("video", StringComparison.Ordinal) ||
            type.Contains("audio", StringComparison.Ordinal) ||
            type.Contains("image", StringComparison.Ordinal) ||
            type.Contains("picture", StringComparison.Ordinal))
        {
            return false;
        }

        return null;
    }

    private static void AddTrashItem(
        ICollection<O2CloudItemDto> output,
        ISet<string> seen,
        JsonElement item,
        bool? folderHint)
    {
        if (item.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var rawType = FirstMediaString(item, "type", "mediatype", "mediaType", "mimetype", "contenttype");
        var isFolder = folderHint ?? LooksLikeTrashFolder(item, rawType);
        var id = isFolder
            ? FirstMediaString(item, "id", "folderid", "folderId", "uuid")
            : MediaIdCandidates(item).FirstOrDefault();
        var name = FirstMediaString(item, "name", "filename", "title");
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        var key = $"{(isFolder ? "folder" : "file")}:{id}";
        if (!seen.Add(key))
        {
            return;
        }

        output.Add(new O2CloudItemDto(
            id,
            name,
            FirstMediaString(item, "parentid", "folderid", "folder", "folderId"),
            isFolder,
            isFolder ? 0 : Math.Max(0, FirstMediaLong(item, "size", "filesize", "fileSize", "contentlength", "contentLength") ?? 0),
            FirstMediaDate(item, "modificationdate", "creationdate", "deleteddate", "uploaded", "date"),
            DirectUrl: AbsoluteMediaUrl(FirstMediaString(item, "url", "downloadurl"), "https://cloud.o2online.es"),
            MediaKind: isFolder ? null : MediaKindFor(name, rawType),
            Fingerprint: FirstMediaString(item, "fingerprint", "hash", "etag", "sha1", "checksum"),
            Node: FirstMediaString(item, "node", "servernode", "serverNode", "storageNode", "storagenode"),
            DownloadToken: FirstMediaString(item, "k", "token", "downloadtoken", "downloadToken", "playbacktoken", "playbackToken")));
    }

    private static bool LooksLikeTrashFolder(JsonElement item, string? rawType)
    {
        var type = rawType?.ToLowerInvariant() ?? string.Empty;
        if (type.Contains("folder", StringComparison.Ordinal) ||
            type.Contains("directory", StringComparison.Ordinal))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(type) &&
            (type.Contains("file", StringComparison.Ordinal) ||
             type.Contains("video", StringComparison.Ordinal) ||
             type.Contains("audio", StringComparison.Ordinal) ||
             type.Contains("image", StringComparison.Ordinal) ||
             type.Contains("picture", StringComparison.Ordinal)))
        {
            return false;
        }

        return false;
    }

    private O2CloudItemDto? FindChild(
        string parentFolderId,
        string name,
        bool isFolder,
        long? expectedSize = null,
        string? expectedId = null)
    {
        var children = ListFolder(parentFolderId);
        if (!string.IsNullOrWhiteSpace(expectedId))
        {
            var idMatch = children.FirstOrDefault(item =>
                item.IsFolder == isFolder &&
                item.Id.Equals(expectedId, StringComparison.OrdinalIgnoreCase));
            if (idMatch is not null)
            {
                return idMatch;
            }
        }

        var matches = children
            .Where(item => item.IsFolder == isFolder && item.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (expectedSize is not null)
        {
            var sizedMatches = matches.Where(item => item.Size == expectedSize.Value).ToList();
            if (sizedMatches.Count > 0)
            {
                matches = sizedMatches;
            }
        }

        return matches
            .OrderByDescending(item => item.ModifiedAt ?? DateTimeOffset.MinValue)
            .FirstOrDefault();
    }

    private O2CloudItemDto? FindChildWithRetries(
        string parentFolderId,
        string name,
        bool isFolder,
        long? expectedSize = null,
        string? expectedId = null,
        Action<int>? onRetry = null)
    {
        for (var attempt = 0; attempt < 96; attempt++)
        {
            var match = string.IsNullOrWhiteSpace(expectedId)
                ? FindChild(parentFolderId, name, isFolder, expectedSize)
                : FindChild(parentFolderId, name, isFolder, expectedSize, expectedId);
            if (match is not null)
            {
                return match;
            }

            if (onRetry is not null && attempt % 4 == 3)
            {
                onRetry((attempt + 1) * 1_250 / 1_000);
            }

            Thread.Sleep(1_250);
        }

        return null;
    }

    private void VerifyMovedToTrash(O2CloudItemDto item)
    {
        Exception? lastError = null;
        for (var attempt = 0; attempt < 16; attempt++)
        {
            try
            {
                var trashItems = ListTrash();
                if (trashItems.Any(candidate =>
                        candidate.IsFolder == item.IsFolder &&
                        (candidate.Id.Equals(item.Id, StringComparison.OrdinalIgnoreCase) ||
                         candidate.Name.Equals(item.Name, StringComparison.OrdinalIgnoreCase))))
                {
                    return;
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or IOException)
            {
                lastError = ex;
            }

            Thread.Sleep(1_000);
        }

        throw new IOException("O2 Cloud acepto el borrado, pero el elemento no aparece confirmado en la papelera remota.", lastError);
    }

    private O2CloudItemDto ResolvePendingUploadForMutation(O2CloudItemDto item)
    {
        if (string.IsNullOrWhiteSpace(item.ParentId))
        {
            throw new IOException("O2 Cloud acepto la subida, pero la app aun no tiene la carpeta remota para operar sobre el archivo.");
        }

        var resolved = FindChildWithRetries(item.ParentId, item.Name, isFolder: false, expectedSize: item.Size);
        return resolved ?? throw new IOException(
            "O2 Cloud acepto la subida, pero el archivo aun no aparece en el listado remoto. Refresca o remonta la unidad antes de borrarlo.");
    }

    private JsonDocument SendJson(
        HttpMethod method,
        string resource,
        IReadOnlyDictionary<string, string?> query,
        object? body = null)
    {
        using var request = new HttpRequestMessage(method, BuildAuthenticatedUri(resource, query));
        ApplySessionHeaders(request);
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        if (body is not null)
        {
            request.Content = new StringContent(JsonSerializer.Serialize(body, JsonOptions), Encoding.UTF8, "application/json");
        }

        using var response = _httpClient.Send(request);
        response.EnsureSuccessStatusCode();
        var text = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        var document = ParseJson(text);
        ThrowIfO2Error(document.RootElement, text);
        return document;
    }

    private JsonDocument? SendFormJson(
        HttpMethod method,
        string resource,
        IReadOnlyDictionary<string, string?> query,
        object body)
    {
        using var request = new HttpRequestMessage(method, BuildAuthenticatedUri(resource, query));
        ApplySessionHeaders(request);
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        request.Content = new FormUrlEncodedContent([
            new KeyValuePair<string, string>("data", JsonSerializer.Serialize(body, JsonOptions)),
        ]);

        using var response = _httpClient.Send(request);
        response.EnsureSuccessStatusCode();
        var text = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        return ParseOptionalJson(text);
    }

    private Uri BuildAuthenticatedUri(string resource, IReadOnlyDictionary<string, string?> query)
    {
        var session = _authService.GetCurrentSession();
        var builder = new UriBuilder(new Uri(_baseUri, resource.TrimStart('/')));
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

    private Uri BuildUploadUri(bool acceptAsynchronous)
    {
        var session = _authService.GetCurrentSession();
        var parameters = new List<string> { "action=save" };
        if (session.IsAuthenticated)
        {
            parameters.Add($"validationkey={Uri.EscapeDataString(session.ValidationKey)}");
        }

        if (acceptAsynchronous)
        {
            parameters.Add("acceptasynchronous=true");
        }

        var builder = new UriBuilder(UploadEndpoint)
        {
            Query = string.Join("&", parameters),
        };
        return builder.Uri;
    }

    private void ApplySessionHeaders(HttpRequestMessage request)
    {
        var session = _authService.GetCurrentSession();
        if (!string.IsNullOrWhiteSpace(session.CookieHeader))
        {
            request.Headers.TryAddWithoutValidation("Cookie", session.CookieHeader);
        }

        request.Headers.TryAddWithoutValidation(
            "User-Agent",
            string.IsNullOrWhiteSpace(session.UserAgent) ? "O2CloudDrive/0.1" : session.UserAgent);
        request.Headers.TryAddWithoutValidation("Origin", _baseUri.GetLeftPart(UriPartial.Authority));
        request.Headers.TryAddWithoutValidation("Referer", _baseUri.GetLeftPart(UriPartial.Authority) + "/");
        request.Headers.TryAddWithoutValidation("Accept-Language", "es-ES,es;q=0.9,en;q=0.8");
        request.Headers.TryAddWithoutValidation("X-deviceid", "O2CloudDrive");
    }

    private string NormalizeShareLink(string rawUrl)
    {
        var value = rawUrl.Trim();
        if (Uri.TryCreate(value, UriKind.Absolute, out var absolute))
        {
            return absolute.ToString();
        }

        var origin = _baseUri.GetLeftPart(UriPartial.Authority);
        return value.StartsWith("/", StringComparison.Ordinal)
            ? origin + value
            : origin + "/" + value.TrimStart('/');
    }

    private static string? ShareUrlFrom(JsonElement root)
    {
        var data = ObjectProperty(root, "data");
        var direct = FirstString(data, "url", "link", "shareurl", "shareUrl") ??
                     FirstString(root, "url", "link", "shareurl", "shareUrl");
        if (!string.IsNullOrWhiteSpace(direct))
        {
            return direct;
        }

        foreach (var container in new[] { data, root })
        {
            foreach (var collectionName in new[] { "links", "sets", "items" })
            {
                var links = ArrayProperty(container, collectionName);
                foreach (var link in links)
                {
                    var value = FirstString(link, "url", "link", "shareurl", "shareUrl");
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        return value;
                    }
                }
            }

            foreach (var name in new[] { "set", "link", "share", "mediaSet" })
            {
                var child = FirstObjectProperty(container, name);
                if (child is not { } element)
                {
                    continue;
                }

                var value = FirstString(element, "url", "link", "shareurl", "shareUrl");
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }
        }

        return null;
    }

    private Uri NormalizeDownloadUri(string rawUrl, string fileName)
    {
        var value = AbsoluteMediaUrl(rawUrl, _baseUri.GetLeftPart(UriPartial.Authority));
        var uri = new Uri(value, UriKind.Absolute);
        var query = QueryParameters(uri);
        if (!uri.AbsolutePath.EndsWith("/sapi/download/video", StringComparison.OrdinalIgnoreCase))
        {
            query["filename"] = fileName;
        }

        var builder = new UriBuilder(uri)
        {
            Query = string.Join("&", query.Select(pair =>
                $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}")),
        };
        return builder.Uri;
    }

    private static string? FirstQueryValue(string? rawUrl, params string[] names)
    {
        if (string.IsNullOrWhiteSpace(rawUrl) ||
            !Uri.TryCreate(rawUrl, UriKind.Absolute, out var uri))
        {
            return null;
        }

        var query = QueryParameters(uri);
        foreach (var name in names)
        {
            if (query.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    private static Dictionary<string, string> QueryParameters(Uri uri)
    {
        var query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pieces = part.Split('=', 2);
            var key = Uri.UnescapeDataString(pieces[0]);
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            query[key] = pieces.Length > 1 ? Uri.UnescapeDataString(pieces[1]) : string.Empty;
        }

        return query;
    }

    private string BuildNativeVideoUrl(string fileName, string node, string token)
    {
        var query = new Dictionary<string, string?>
        {
            ["action"] = "get",
            ["k"] = token,
            ["node"] = node,
        };

        var builder = new UriBuilder(new Uri(_baseUri, "download/video"))
        {
            Query = string.Join("&", query
                .Where(pair => !string.IsNullOrWhiteSpace(pair.Value))
                .Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value!)}")),
        };
        return builder.Uri.ToString();
    }

    private static string AbsoluteMediaUrl(string? raw, string mediaServer)
    {
        var value = raw?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        if (value.StartsWith("//", StringComparison.Ordinal))
        {
            return "https:" + value;
        }

        if (value.StartsWith("/", StringComparison.Ordinal))
        {
            if (value.StartsWith("/sapi/", StringComparison.OrdinalIgnoreCase))
            {
                return "https://cloud.o2online.es" + value;
            }

            return mediaServer.TrimEnd('/') + value;
        }

        return value;
    }

    private static JsonDocument? ParseOptionalJson(string text)
    {
        var trimmed = text.Trim();
        if (string.IsNullOrWhiteSpace(trimmed) ||
            trimmed.Equals("0", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("ok", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("success", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var document = ParseJson(trimmed);
        ThrowIfO2Error(document.RootElement, trimmed);
        return document;
    }

    private static bool IsUploadAccepted(JsonDocument? document, string rawText)
    {
        var trimmed = rawText.Trim();
        if (string.IsNullOrWhiteSpace(trimmed) ||
            trimmed.Equals("0", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("ok", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("success", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("success", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return document is not null && IsUploadAcceptedObject(document.RootElement);
    }

    private static bool IsUploadAcceptedObject(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var data = FirstObject(element, "data");
        if (data is { } nested && IsUploadAcceptedObject(nested))
        {
            return true;
        }

        var success = FirstString(element, "success", "ok", "result")?.ToLowerInvariant();
        if (success is "true" or "success" or "ok")
        {
            return true;
        }

        var status = FirstString(element, "status")?.ToLowerInvariant();
        if (status is "success" or "ok" or "accepted")
        {
            return true;
        }

        var code = FirstString(element, "code", "resultcode", "statuscode")?.ToUpperInvariant();
        return code is "0" or "OK" or "SUCCESS" or "COM-0000" or "COM-0";
    }

    private static JsonElement? FirstObject(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object)
            {
                return value;
            }
        }

        return null;
    }

    private static O2CloudItemDto PendingUploadPlaceholder(string parentFolderId, string name, long size)
    {
        return new O2CloudItemDto(
            PendingUploadIdPrefix + Guid.NewGuid().ToString("N"),
            name,
            parentFolderId,
            false,
            Math.Max(0, size),
            DateTimeOffset.UtcNow,
            DirectUrl: null,
            MediaKind: MediaKindFor(name, null),
            Fingerprint: null,
            Node: null,
            DownloadToken: null);
    }

    private static bool IsPendingUploadId(string id)
    {
        return id.StartsWith(PendingUploadIdPrefix, StringComparison.OrdinalIgnoreCase);
    }

    private static JsonDocument ParseJson(string text)
    {
        var trimmed = text.Trim();
        try
        {
            return JsonDocument.Parse(trimmed);
        }
        catch (JsonException)
        {
            var start = trimmed.IndexOf('{');
            var end = trimmed.LastIndexOf('}');
            if (start >= 0 && end > start)
            {
                return JsonDocument.Parse(trimmed[start..(end + 1)]);
            }

            throw;
        }
    }

    private static O2CloudItemDto? TryParseItem(
        JsonDocument? document,
        string parentFolderId,
        bool? expectedIsFolder,
        string fallbackName)
    {
        if (document is null)
        {
            return null;
        }

        foreach (var candidate in ObjectCandidates(document.RootElement))
        {
            var id = expectedIsFolder switch
            {
                true => FirstString(candidate, "id", "folderid", "folderId", "uuid"),
                false => FirstString(candidate, "id", "mediaid", "fdoid", "uuid"),
                _ => FirstString(candidate, "id", "folderid", "folderId", "mediaid", "fdoid", "uuid"),
            };
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            var name = FirstString(candidate, "name", "filename") ?? fallbackName;
            var rawType = FirstString(candidate, "type", "mediatype", "mimetype", "contenttype");
            var isFolder = expectedIsFolder ?? rawType?.Contains("folder", StringComparison.OrdinalIgnoreCase) == true;
            return new O2CloudItemDto(
                id,
                name,
                FirstString(candidate, "parentid", "folderid", "folder", "folderId") ?? parentFolderId,
                IsFolder: isFolder,
                Size: isFolder ? 0 : Math.Max(0, FirstLong(candidate, "size", "filesize", "fileSize", "contentlength", "contentLength") ?? 0),
                ModifiedAt: FirstDate(candidate, "modificationdate", "creationdate", "uploaded", "date"),
                DirectUrl: AbsoluteMediaUrl(FirstString(candidate, "url", "viewurl", "downloadurl"), "https://cloud.o2online.es"),
                MediaKind: isFolder ? null : MediaKindFor(name, rawType),
                Fingerprint: FirstString(candidate, "fingerprint", "hash", "etag", "sha1", "checksum"),
                Node: FirstString(candidate, "node"),
                DownloadToken: FirstString(candidate, "k", "token"));
        }

        return null;
    }

    private static IEnumerable<JsonElement> ObjectCandidates(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            yield return element;
            foreach (var property in element.EnumerateObject())
            {
                foreach (var child in ObjectCandidates(property.Value))
                {
                    yield return child;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                foreach (var child in ObjectCandidates(item))
                {
                    yield return child;
                }
            }
        }
    }

    private static JsonElement ObjectProperty(JsonElement element, string name)
    {
        return element.ValueKind == JsonValueKind.Object &&
               element.TryGetProperty(name, out var property) &&
               property.ValueKind == JsonValueKind.Object
            ? property
            : default;
    }

    private static JsonElement? FirstObjectProperty(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Object)
            {
                return property;
            }
        }

        return null;
    }

    private static IReadOnlyList<JsonElement> ArrayProperty(JsonElement element, string name)
    {
        return element.ValueKind == JsonValueKind.Object &&
               element.TryGetProperty(name, out var property) &&
               property.ValueKind == JsonValueKind.Array
            ? property.EnumerateArray().ToList()
            : [];
    }

    private static IReadOnlyList<JsonElement> FirstArray(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            var array = ArrayProperty(element, name);
            if (array.Count > 0)
            {
                return array;
            }
        }

        return [];
    }

    private static string PageSignature(
        IReadOnlyList<JsonElement> elements,
        Func<JsonElement, IReadOnlyList<string>> keySelector)
    {
        if (elements.Count == 0)
        {
            return "<empty>";
        }

        return string.Join("|", elements.Select((element, index) =>
        {
            var keys = keySelector(element);
            if (keys.Count > 0)
            {
                return string.Join(",", keys);
            }

            return FirstMediaString(element, "name", "filename", "title")
                ?? $"{index}:{element.GetRawText().Length.ToString(CultureInfo.InvariantCulture)}";
        }));
    }

    private static string? FirstString(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var property))
            {
                continue;
            }

            var value = property.ValueKind switch
            {
                JsonValueKind.String => property.GetString(),
                JsonValueKind.Number => property.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => null,
            };
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    private static string? FirstMediaString(JsonElement element, params string[] names)
    {
        foreach (var candidate in MediaObjectViews(element))
        {
            var value = FirstString(candidate, names);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    private static long? FirstMediaLong(JsonElement element, params string[] names)
    {
        foreach (var candidate in MediaObjectViews(element))
        {
            var value = FirstLong(candidate, names);
            if (value is not null)
            {
                return value;
            }
        }

        return null;
    }

    private static DateTimeOffset? FirstMediaDate(JsonElement element, params string[] names)
    {
        foreach (var candidate in MediaObjectViews(element))
        {
            var value = FirstDate(candidate, names);
            if (value is not null)
            {
                return value;
            }
        }

        return null;
    }

    private static IReadOnlyList<string> FolderIdCandidates(JsonElement element)
    {
        return new[]
        {
            FirstString(element, "id"),
            FirstString(element, "folderid"),
            FirstString(element, "folderId"),
            FirstString(element, "uuid"),
        }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IReadOnlyList<string> MediaIdCandidates(JsonElement element)
    {
        return MediaObjectViews(element)
            .SelectMany(candidate => new[]
            {
                FirstString(candidate, "id"),
                FirstString(candidate, "mediaid"),
                FirstString(candidate, "mediaId"),
                FirstString(candidate, "fdoid"),
                FirstString(candidate, "uuid"),
            })
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static JsonElement MediaDetailFor(
        JsonElement element,
        IReadOnlyDictionary<string, JsonElement> details)
    {
        foreach (var id in MediaIdCandidates(element))
        {
            if (details.TryGetValue(id, out var detail))
            {
                return detail;
            }
        }

        return default;
    }

    private static bool LooksLikeFolder(JsonElement element)
    {
        var rawType = FirstMediaString(element, "type", "mediatype", "mediaType", "mimetype", "contenttype", "kind")
            ?.ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(rawType) &&
            (rawType.Contains("folder", StringComparison.Ordinal) ||
             rawType.Contains("directory", StringComparison.Ordinal)))
        {
            return true;
        }

        return MediaObjectViews(element).Any(candidate =>
            ArrayProperty(candidate, "folders").Count > 0 ||
            ArrayProperty(candidate, "directories").Count > 0);
    }

    private static IEnumerable<JsonElement> MediaObjectViews(JsonElement element, int depth = 0)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            yield break;
        }

        yield return element;

        if (depth >= 2)
        {
            yield break;
        }

        foreach (var name in MediaObjectContainers)
        {
            if (!element.TryGetProperty(name, out var property))
            {
                continue;
            }

            if (property.ValueKind == JsonValueKind.Object)
            {
                foreach (var nested in MediaObjectViews(property, depth + 1))
                {
                    yield return nested;
                }
            }
            else if (property.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in property.EnumerateArray())
                {
                    foreach (var nested in MediaObjectViews(item, depth + 1))
                    {
                        yield return nested;
                    }
                }
            }
        }
    }

    private static bool IsRecoverableFolderIdError(Exception exception)
    {
        var text = exception.ToString();
        return text.Contains("COM-1021", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("Invalid parameter value", StringComparison.OrdinalIgnoreCase) ||
               exception is HttpRequestException;
    }

    private static bool IsUnsupportedOperation(Exception exception)
    {
        var text = exception.ToString();
        return text.Contains("COM-1005", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("COM-1021", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("unsupported operation", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("Invalid parameter value", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRecoverableDownloadResolutionError(Exception exception)
    {
        return exception is InvalidOperationException ||
               exception is HttpRequestException;
    }

    private static bool IsRecoverableDownloadUrlError(HttpRequestException exception)
    {
        return exception.StatusCode is HttpStatusCode.OK or
            HttpStatusCode.BadRequest or
            HttpStatusCode.Unauthorized or
            HttpStatusCode.Forbidden or
            HttpStatusCode.NotFound or
            HttpStatusCode.Gone or
            HttpStatusCode.TooManyRequests or
            HttpStatusCode.InternalServerError or
            HttpStatusCode.BadGateway or
            HttpStatusCode.ServiceUnavailable or
            HttpStatusCode.GatewayTimeout;
    }

    private static long? FirstLong(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var property))
            {
                continue;
            }

            if (property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out var longValue))
            {
                return longValue;
            }

            if (property.ValueKind == JsonValueKind.String)
            {
                var parsed = ParseByteCount(property.GetString());
                if (parsed is not null)
                {
                    return parsed;
                }
            }
        }

        return null;
    }

    private static long? FirstLongRecursive(JsonElement element, params string[] names)
    {
        var direct = FirstLong(element, names);
        if (direct is not null)
        {
            return direct;
        }

        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                var child = FirstLongRecursive(property.Value, names);
                if (child is not null)
                {
                    return child;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var child = FirstLongRecursive(item, names);
                if (child is not null)
                {
                    return child;
                }
            }
        }

        return null;
    }

    private static long? ParseByteCount(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var value = raw.Trim().Replace('\u00A0', ' ');
        if (long.TryParse(value, NumberStyles.Integer | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var integer))
        {
            return integer;
        }

        var match = Regex.Match(value, @"(?<number>\d+(?:[\.,]\d+)?)\s*(?<unit>b|bytes|kb|kib|mb|mib|gb|gib|tb|tib)?", RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            return null;
        }

        var numberText = match.Groups["number"].Value.Replace(',', '.');
        if (!double.TryParse(numberText, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
        {
            return null;
        }

        var unit = match.Groups["unit"].Value.ToLowerInvariant();
        var factor = unit switch
        {
            "kb" or "kib" => 1024d,
            "mb" or "mib" => 1024d * 1024d,
            "gb" or "gib" => 1024d * 1024d * 1024d,
            "tb" or "tib" => 1024d * 1024d * 1024d * 1024d,
            _ => 1d,
        };

        var bytes = number * factor;
        return bytes >= long.MaxValue ? long.MaxValue : (long)Math.Round(bytes);
    }

    private static DateTimeOffset? FirstDate(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var property))
            {
                continue;
            }

            if (property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out var number))
            {
                if (number > 10_000_000_000)
                {
                    return DateTimeOffset.FromUnixTimeMilliseconds(number);
                }

                if (number > 0)
                {
                    return DateTimeOffset.FromUnixTimeSeconds(number);
                }
            }

            if (property.ValueKind == JsonValueKind.String)
            {
                var value = property.GetString();
                if (DateTimeOffset.TryParse(value, out var parsed))
                {
                    return parsed;
                }

                if (long.TryParse(value, out var parsedNumber))
                {
                    return parsedNumber > 10_000_000_000
                        ? DateTimeOffset.FromUnixTimeMilliseconds(parsedNumber)
                        : DateTimeOffset.FromUnixTimeSeconds(parsedNumber);
                }
            }
        }

        return null;
    }

    private static bool BoolProperty(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var property))
        {
            return false;
        }

        return property.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.String => bool.TryParse(property.GetString(), out var value) && value,
            _ => false,
        };
    }

    private static string MediaKindFor(string name, string? rawType)
    {
        var type = rawType?.ToLowerInvariant() ?? string.Empty;
        if (type.Contains("video", StringComparison.Ordinal)) return "video";
        if (type.Contains("audio", StringComparison.Ordinal)) return "audio";
        if (type.Contains("picture", StringComparison.Ordinal) || type.Contains("image", StringComparison.Ordinal)) return "picture";

        var extension = Path.GetExtension(name).TrimStart('.').ToLowerInvariant();
        if (new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "mkv", "mp4", "avi", "mov", "m4v", "webm", "ts", "mpeg", "mpg", "m3u8",
            }.Contains(extension))
        {
            return "video";
        }

        if (new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "mp3", "aac", "m4a", "flac", "wav", "ogg", "opus", "wma",
            }.Contains(extension))
        {
            return "audio";
        }

        if (new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "jpg", "jpeg", "png", "gif", "webp", "bmp", "heic", "tif", "tiff",
            }.Contains(extension))
        {
            return "picture";
        }

        return "file";
    }

    private static string NormalizeMediaKind(string mediaKind)
    {
        var value = mediaKind.Trim().ToLowerInvariant();
        return value switch
        {
            "image" => "picture",
            "track" => "audio",
            "picture" or "video" or "audio" or "file" => value,
            _ => "file",
        };
    }

    private static bool IsVideo(O2CloudItemDto item)
    {
        return string.Equals(NormalizeMediaKind(item.MediaKind ?? MediaKindFor(item.Name, null)), "video", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPlayable(O2CloudItemDto item)
    {
        var mediaKind = NormalizeMediaKind(item.MediaKind ?? MediaKindFor(item.Name, null));
        return string.Equals(mediaKind, "video", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(mediaKind, "audio", StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<string> DeletePayloadNamesFor(string mediaKind, bool softDelete)
    {
        return NormalizeMediaKind(mediaKind) switch
        {
            "picture" => ["pictures"],
            "video" => ["videos"],
            "audio" when softDelete => ["audios", "tracks"],
            "audio" => ["audios"],
            _ => ["files"],
        };
    }

    private static string? ContentTypeFor(string name)
    {
        return Path.GetExtension(name).ToLowerInvariant() switch
        {
            ".txt" => "text/plain",
            ".csv" => "text/csv",
            ".json" => "application/json",
            ".pdf" => "application/pdf",
            ".zip" => "application/zip",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".mp4" => "video/mp4",
            ".mkv" => "video/x-matroska",
            ".mov" => "video/quicktime",
            ".mp3" => "audio/mpeg",
            ".m4a" => "audio/mp4",
            ".wav" => "audio/wav",
            _ => null,
        };
    }

    private static object O2Id(string id)
    {
        return long.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : id;
    }

    private static void ThrowIfO2Error(JsonElement root, string rawText)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (root.TryGetProperty("error", out var error) && IsMeaningfulError(error))
        {
            throw new InvalidOperationException($"O2 Cloud devolvio error: {TrimPreview(error.ToString())}");
        }

        var success = FirstString(root, "success");
        if (success is not null && success.Equals("false", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"O2 Cloud rechazo la operacion: {TrimPreview(rawText)}");
        }

        var status = FirstString(root, "status");
        if (status is not null &&
            (status.Contains("error", StringComparison.OrdinalIgnoreCase) ||
             status.Contains("fail", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"O2 Cloud rechazo la operacion: {TrimPreview(rawText)}");
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

    private static bool HasMeaningfulChanges(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Object => element.EnumerateObject().Any(property => HasMeaningfulChanges(property.Value)),
            JsonValueKind.Array => element.GetArrayLength() > 0,
            JsonValueKind.String => !string.IsNullOrWhiteSpace(element.GetString()),
            JsonValueKind.Number => element.GetRawText() != "0",
            JsonValueKind.True => true,
            _ => false,
        };
    }

    private static IReadOnlySet<string> ChangeIds(JsonElement data, string containerName)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var container = ObjectProperty(data, containerName);
        if (container.ValueKind != JsonValueKind.Object)
        {
            return ids;
        }

        foreach (var property in container.EnumerateObject())
        {
            AddChangeIds(property.Value, ids);
        }

        return ids;
    }

    private static bool HasNewChanges(JsonElement data, string containerName)
    {
        var container = ObjectProperty(data, containerName);
        return container.ValueKind == JsonValueKind.Object &&
               container.TryGetProperty("N", out var created) &&
               HasMeaningfulChanges(created);
    }

    private static void AddChangeIds(JsonElement element, ISet<string> ids)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    AddChangeIds(item, ids);
                }

                break;
            case JsonValueKind.Object:
                var id = FirstString(element, "id", "folderid", "fileid", "nodeid") ??
                         FirstLong(element, "id", "folderid", "fileid", "nodeid")?.ToString(CultureInfo.InvariantCulture);
                if (!string.IsNullOrWhiteSpace(id))
                {
                    ids.Add(id);
                }

                break;
            case JsonValueKind.Number:
                ids.Add(element.GetRawText());
                break;
            case JsonValueKind.String:
                var value = element.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    ids.Add(value);
                }

                break;
        }
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

    private static bool IsSuccessValue(string value)
    {
        var normalized = value.Trim().ToUpperInvariant();
        return normalized is "0" or "OK" or "SUCCESS" or "TRUE" or "COM-0000" or "COM-0";
    }

    private static string TrimPreview(string value)
    {
        var compact = value.ReplaceLineEndings(" ").Trim();
        return compact.Length <= 240 ? compact : compact[..240] + "...";
    }

    private static T FirstSuccess<T>(params Func<T>[] operations)
    {
        Exception? lastError = null;
        foreach (var operation in operations)
        {
            try
            {
                return operation();
            }
            catch (Exception ex)
            {
                lastError = ex;
            }
        }

        throw new InvalidOperationException("O2 Cloud rechazo la operacion.", lastError);
    }

    private static void FirstSuccess(params Action[] operations)
    {
        Exception? lastError = null;
        foreach (var operation in operations)
        {
            try
            {
                operation();
                return;
            }
            catch (Exception ex)
            {
                lastError = ex;
            }
        }

        throw new InvalidOperationException("O2 Cloud rechazo la operacion.", lastError);
    }

    private HttpResponseMessage SendUploadRequest(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        try
        {
            return _httpClient.Send(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                "O2 Cloud no recibio datos de la subida durante 60 segundos. La subida se ha cancelado para liberar la unidad.",
                ex);
        }
    }

    private void ReportTransfer(
        string phase,
        string fileName,
        long bytesTransferred,
        long totalBytes,
        long bytesPerSecond = 0,
        string? message = null)
    {
        TransferProgress?.Invoke(this, new O2TransferProgress(phase, fileName, bytesTransferred, totalBytes, bytesPerSecond, message));
    }

    private static string UploadFailureMessage(Exception exception)
    {
        return exception switch
        {
            TimeoutException => exception.Message,
            TaskCanceledException => "La subida se interrumpio por timeout o por la conexion HTTP antes de terminar.",
            HttpRequestException => "La conexion con O2 Cloud se interrumpio durante la subida.",
            IOException { InnerException: HttpRequestException } => "La conexion con O2 Cloud se interrumpio durante la subida.",
            IOException { InnerException: TaskCanceledException } => "La subida se interrumpio por timeout o por la conexion HTTP antes de terminar.",
            _ => exception.Message,
        };
    }

    private static void Dispose(IDisposable? disposable)
    {
        disposable?.Dispose();
    }

    private sealed class ProgressStreamContent : HttpContent
    {
        private const int BufferSize = 256 * 1024;
        private const long ReportEveryBytes = 512L * 1024L;
        private static readonly TimeSpan ReportEveryTime = TimeSpan.FromSeconds(2);
        private readonly Stream _source;
        private readonly long _length;
        private readonly Action<long, long, long> _progress;
        private long _lastReportedBytes;
        private DateTimeOffset _startedAt;
        private DateTimeOffset _lastReportedAt;

        public ProgressStreamContent(Stream source, long length, Action<long, long, long> progress)
        {
            _source = source;
            _length = length;
            _progress = progress;
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            return SerializeToStreamAsync(stream, context, CancellationToken.None);
        }

        protected override void SerializeToStream(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            var buffer = new byte[BufferSize];
            long sent = 0;
            StartProgress();

            int read;
            while ((read = _source.Read(buffer, 0, buffer.Length)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                stream.Write(buffer, 0, read);
                sent += read;
                ReportProgressIfNeeded(sent);
            }

            ReportFinalProgress(sent);
        }

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            var buffer = new byte[BufferSize];
            long sent = 0;
            StartProgress();

            int read;
            while ((read = await _source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false)) > 0)
            {
                await stream.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                sent += read;
                ReportProgressIfNeeded(sent);
            }

            ReportFinalProgress(sent);
        }

        private void StartProgress()
        {
            _startedAt = DateTimeOffset.UtcNow;
            _lastReportedAt = _startedAt;
            _lastReportedBytes = 0;
            _progress(0, _length, 0);
        }

        private void ReportProgressIfNeeded(long sent)
        {
            var now = DateTimeOffset.UtcNow;
            if (sent == _length ||
                sent - _lastReportedBytes >= ReportEveryBytes ||
                now - _lastReportedAt >= ReportEveryTime)
            {
                _lastReportedBytes = sent;
                _lastReportedAt = now;
                _progress(sent, _length, CalculateSpeed(sent, now));
            }
        }

        private void ReportFinalProgress(long sent)
        {
            if (_lastReportedBytes != sent)
            {
                _progress(sent, _length, CalculateSpeed(sent, DateTimeOffset.UtcNow));
            }
        }

        private long CalculateSpeed(long sent, DateTimeOffset now)
        {
            var elapsedSeconds = Math.Max(0.001d, (now - _startedAt).TotalSeconds);
            return (long)Math.Max(0d, sent / elapsedSeconds);
        }

        protected override bool TryComputeLength(out long length)
        {
            length = _length;
            return true;
        }
    }
}
