namespace O2CloudDrive.Caching;

public interface ILocalCacheService
{
    string GetCachedFilePath(string itemId);
    Task<byte[]?> TryReadAsync(string itemId, CancellationToken cancellationToken);
    Task WriteAsync(string itemId, byte[] content, CancellationToken cancellationToken);
    Task DeleteAsync(string itemId, CancellationToken cancellationToken);
}
