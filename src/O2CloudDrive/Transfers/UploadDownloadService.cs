using O2CloudDrive.Caching;
using O2CloudDrive.VirtualFileSystem;

namespace O2CloudDrive.Transfers;

public interface IUploadDownloadService
{
    Task<byte[]> DownloadAsync(CloudNode file, CancellationToken cancellationToken);
    Task UploadAsync(CloudNode file, byte[] content, CancellationToken cancellationToken);
}

public sealed class UploadDownloadService : IUploadDownloadService
{
    private readonly ILocalCacheService _cacheService;
    private readonly ICloudFileStore _store;

    public UploadDownloadService(ILocalCacheService cacheService, ICloudFileStore store)
    {
        _cacheService = cacheService;
        _store = store;
    }

    public async Task<byte[]> DownloadAsync(CloudNode file, CancellationToken cancellationToken)
    {
        var cached = await _cacheService.TryReadAsync(file.Id, cancellationToken);
        if (cached is not null)
        {
            return cached;
        }

        var content = _store.ReadAllBytes(file);
        await _cacheService.WriteAsync(file.Id, content, cancellationToken);
        return content;
    }

    public async Task UploadAsync(CloudNode file, byte[] content, CancellationToken cancellationToken)
    {
        _store.ReplaceContent(file, content);
        await _cacheService.WriteAsync(file.Id, content, cancellationToken);
    }
}
