using System.Collections.Concurrent;
using O2CloudDrive.Api;
using O2CloudDrive.VirtualFileSystem;

namespace O2CloudDrive.Tests;

public sealed class O2CloudFileStoreReadCacheTests : IDisposable
{
    private const int BlockSize = 256 * 1024;
    private readonly string _cacheDirectory = Path.Combine(Path.GetTempPath(), "O2CloudDrive.Tests", Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(BlockSize, 4)]
    [InlineData(ReadCacheOptions.DefaultBlockSizeBytes, 1)]
    public void SmallSequentialReads_DownloadEachBlockOnceAndReuseCachedSlices(int blockSize, int fullBlocks)
    {
        var content = Pattern(blockSize * fullBlocks + 133);
        var api = new ReadOnlyApiClient(content);
        var (store, file) = CreateStore(api, blockSize);

        for (var pass = 0; pass < 2; pass++)
        {
            using var received = new MemoryStream();
            for (var offset = 0; offset < content.Length; offset += 64 * 1024)
            {
                var bytes = store.ReadBytes(file, (ulong)offset, 64 * 1024);
                received.Write(bytes);
            }

            Assert.Equal(content, received.ToArray());
            Assert.Equal(fullBlocks + 1, api.Downloads.Count);
        }

        Assert.All(api.Downloads.Take(fullBlocks), request => Assert.Equal((uint)blockSize, request.Length));
        Assert.Equal(133U, api.Downloads.Last().Length);
    }

    [Fact]
    public void SmallReadAcrossBlockBoundary_ReturnsOnlyRequestedBytes()
    {
        var content = Pattern(BlockSize * 2 + 133);
        var api = new ReadOnlyApiClient(content);
        var (store, file) = CreateStore(api);

        Assert.Equal(content.AsSpan(BlockSize - 7, 19).ToArray(), store.ReadBytes(file, BlockSize - 7, 19));
        Assert.Equal(content.AsSpan(BlockSize - 3, 11).ToArray(), store.ReadBytes(file, BlockSize - 3, 11));
        Assert.Equal(2, api.Downloads.Count);
    }

    [Fact]
    public void TruncatedCachedBlock_IsDownloadedAgainBeforeReturningData()
    {
        var content = Pattern(BlockSize + 133);
        var api = new ReadOnlyApiClient(content);
        var (store, file) = CreateStore(api);
        Assert.Equal(content.AsSpan(0, 31).ToArray(), store.ReadBytes(file, 0, 31));
        var blockPath = Assert.Single(Directory.EnumerateFiles(_cacheDirectory, "*.blk", SearchOption.AllDirectories));
        File.WriteAllBytes(blockPath, [0]);

        Assert.Equal(content.AsSpan(17, 31).ToArray(), store.ReadBytes(file, 17, 31));
        Assert.Equal(2, api.Downloads.Count);
        Assert.Equal(BlockSize, new FileInfo(blockPath).Length);
    }

    [Fact]
    public void PersistentCache_DoesNotReusePreviousFingerprintWithSameIdSizeAndDate()
    {
        var version1 = Pattern(BlockSize);
        var firstApi = new ReadOnlyApiClient(version1, "version-1");
        var (firstStore, firstFile) = CreateStore(firstApi);
        Assert.Equal(version1.AsSpan(0, 64).ToArray(), firstStore.ReadBytes(firstFile, 0, 64));

        var version2 = version1.Select(value => (byte)(value ^ 255)).ToArray();
        var secondApi = new ReadOnlyApiClient(version2, "version-2");
        var (secondStore, secondFile) = CreateStore(secondApi);

        Assert.Equal(version2.AsSpan(0, 64).ToArray(), secondStore.ReadBytes(secondFile, 0, 64));
        Assert.Single(firstApi.Downloads);
        Assert.Single(secondApi.Downloads);
    }

    [Fact]
    public async Task ConcurrentSmallReadsOfSameColdBlock_ShareOneDownload()
    {
        var content = Pattern(BlockSize);
        var api = new ReadOnlyApiClient(content) { DownloadDelay = TimeSpan.FromMilliseconds(50) };
        var (store, file) = CreateStore(api);

        await Task.WhenAll(Enumerable.Range(0, 12).Select(index => Task.Run(() =>
        {
            var offset = index * 79;
            Assert.Equal(content.AsSpan(offset, 64).ToArray(), store.ReadBytes(file, (ulong)offset, 64));
        })));

        Assert.Single(api.Downloads);
    }

    public void Dispose()
    {
        if (Directory.Exists(_cacheDirectory))
        {
            Directory.Delete(_cacheDirectory, recursive: true);
        }
    }

    private (O2CloudFileStore Store, CloudNode File) CreateStore(ReadOnlyApiClient api, int blockSize = BlockSize)
    {
        var store = new O2CloudFileStore(api, new ReadCacheOptions(_cacheDirectory, blockSize, 0, 0));
        return (store, Assert.Single(store.GetChildren(store.Root), node => !node.IsDirectory));
    }

    private static byte[] Pattern(int length) => Enumerable.Range(0, length).Select(index => (byte)(index % 251)).ToArray();

    private sealed class ReadOnlyApiClient(byte[] content, string fingerprint = "version-1") : IO2CloudApiClient
    {
        public ConcurrentQueue<(ulong Offset, uint Length)> Downloads { get; } = new();
        public TimeSpan DownloadDelay { get; init; }
        public O2CloudItemDto GetRootFolder() => new("root-test", string.Empty, null, true, 0, null, null, null, null, null, null);
        public IReadOnlyList<O2CloudItemDto> ListFolder(string folderId) =>
            [new("file-test", "cached.dat", "root-test", false, content.LongLength, DateTimeOffset.UnixEpoch, null, "file", fingerprint, null, null)];

        public byte[] DownloadFile(O2CloudItemDto item, ulong offset, uint length)
        {
            Downloads.Enqueue((offset, length));
            if (DownloadDelay > TimeSpan.Zero)
            {
                Thread.Sleep(DownloadDelay);
            }

            var count = checked((int)Math.Min(length, (ulong)content.Length - offset));
            return content.AsSpan(checked((int)offset), count).ToArray();
        }

        public O2StorageInfo GetStorageInfo() => throw new NotSupportedException();
        public bool KeepSessionAlive() => throw new NotSupportedException();
        public O2ChangeSummary GetChangesSince(long from) => throw new NotSupportedException();
        public IReadOnlyList<O2CloudItemDto> ListTrash() => throw new NotSupportedException();
        public string GetShareLink(O2CloudItemDto item) => throw new NotSupportedException();
        public O2CloudItemDto CreateFolder(string parentFolderId, string name) => throw new NotSupportedException();
        public O2CloudItemDto UploadFile(string parentFolderId, string name, byte[] bytes) => throw new NotSupportedException();
        public O2CloudItemDto UploadFile(string parentFolderId, string name, Stream stream, long contentLength) => throw new NotSupportedException();
        public O2CloudItemDto ReplaceFile(O2CloudItemDto previous, string parentFolderId, string name, Stream stream, long contentLength) => throw new NotSupportedException();
        public void RenameOrMove(O2CloudItemDto item, string newName, string parentFolderId) => throw new NotSupportedException();
        public void MoveToTrash(O2CloudItemDto item) => throw new NotSupportedException();
        public O2CloudItemDto RestoreFromTrash(O2CloudItemDto item) => throw new NotSupportedException();
        public void PermanentlyDelete(O2CloudItemDto item) => throw new NotSupportedException();
    }
}
