namespace O2CloudDrive.Caching;

public sealed class LocalCacheService : ILocalCacheService
{
    private readonly string _cacheDirectory;

    public LocalCacheService(string cacheDirectory)
    {
        _cacheDirectory = cacheDirectory;
        Directory.CreateDirectory(_cacheDirectory);
    }

    public string GetCachedFilePath(string itemId)
    {
        var safeName = string.Join("_", itemId.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
        return Path.Combine(_cacheDirectory, $"{safeName}.bin");
    }

    public async Task<byte[]?> TryReadAsync(string itemId, CancellationToken cancellationToken)
    {
        var path = GetCachedFilePath(itemId);
        return File.Exists(path) ? await File.ReadAllBytesAsync(path, cancellationToken) : null;
    }

    public async Task WriteAsync(string itemId, byte[] content, CancellationToken cancellationToken)
    {
        var path = GetCachedFilePath(itemId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, content, cancellationToken);
    }

    public Task DeleteAsync(string itemId, CancellationToken cancellationToken)
    {
        var path = GetCachedFilePath(itemId);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return Task.CompletedTask;
    }
}
