namespace O2CloudDrive.VirtualFileSystem;

public sealed record ReadCacheOptions(
    string CacheDirectory,
    int BlockSizeBytes,
    int ReadAheadBlocks,
    long MaxBytes)
{
    public const int DefaultBlockSizeBytes = 4 * 1024 * 1024;
    public const int DefaultReadAheadBlocks = 3;
    public const long DefaultMaxBytes = 20L * 1024L * 1024L * 1024L;

    public static ReadCacheOptions Default { get; } = new(
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "O2CloudDrive",
            "Cache"),
        DefaultBlockSizeBytes,
        DefaultReadAheadBlocks,
        DefaultMaxBytes);
}
