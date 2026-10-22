using O2CloudDrive.Api;
using O2CloudDrive.VirtualFileSystem;

namespace O2CloudDrive.Tests;

public sealed class O2CloudFileStoreVolumeTests : IDisposable
{
    private readonly string _cacheDirectory = Path.Combine(Path.GetTempPath(), "O2CloudDrive.Tests", Guid.NewGuid().ToString("N"));
    private readonly ManualTimeProvider _time = new();

    [Fact]
    public void GetVolumeInfo_ReusesValidQuotaForThirtySeconds()
    {
        var api = new StorageApiClient();
        var store = CreateStore(api);
        var first = store.GetVolumeInfo();
        Assert.Equal(1, api.StorageCalls);

        api.ReadStorage = () => new O2StorageInfo(50, 100, 50);
        _time.Advance(TimeSpan.FromSeconds(29));
        Assert.Equal(first, store.GetVolumeInfo());
        Assert.Equal(1, api.StorageCalls);

        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(50UL, store.GetVolumeInfo().FreeSize);
        Assert.Equal(2, api.StorageCalls);
    }

    [Fact]
    public void GetVolumeInfo_WhenInitialRequestFails_BoundsRetryFrequency()
    {
        var api = new StorageApiClient { ReadStorage = () => throw new IOException("Injected quota failure.") };
        var store = CreateStore(api);
        var fallback = store.GetVolumeInfo();
        Assert.True(fallback.TotalSize > 0);

        for (var index = 0; index < 30; index++)
        {
            Assert.Equal(fallback, store.GetVolumeInfo());
        }

        Assert.Equal(1, api.StorageCalls);
        api.ReadStorage = () => new O2StorageInfo(40, 100, 60);
        _time.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(60UL, store.GetVolumeInfo().FreeSize);
        Assert.Equal(2, api.StorageCalls);
    }

    [Fact]
    public void GetVolumeInfo_WhenRefreshFails_RetainsLastQuotaAndWaitsBeforeRetry()
    {
        var api = new StorageApiClient();
        var store = CreateStore(api);
        var last = store.GetVolumeInfo();
        _time.Advance(TimeSpan.FromSeconds(30));
        api.ReadStorage = () => throw new IOException("Injected quota refresh failure.");

        Assert.Equal(last, store.GetVolumeInfo());
        _time.Advance(TimeSpan.FromSeconds(4));
        Assert.Equal(last, store.GetVolumeInfo());
        Assert.Equal(2, api.StorageCalls);

        api.ReadStorage = () => new O2StorageInfo(50, 100, 50);
        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(50UL, store.GetVolumeInfo().FreeSize);
        Assert.Equal(3, api.StorageCalls);
    }

    [Fact]
    public async Task GetVolumeInfo_ConcurrentRequestsReturnLastQuotaDuringSingleRefresh()
    {
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var api = new StorageApiClient();
        var store = CreateStore(api);
        var last = store.GetVolumeInfo();
        _time.Advance(TimeSpan.FromSeconds(30));
        api.ReadStorage = () =>
        {
            started.Set();
            if (!release.Wait(TimeSpan.FromSeconds(5)))
            {
                throw new TimeoutException("The test did not release the quota request.");
            }

            return new O2StorageInfo(50, 100, 50);
        };

        var refresh = Task.Run(store.GetVolumeInfo);
        try
        {
            Assert.True(started.Wait(TimeSpan.FromSeconds(5)));
            var parallelCalls = await Task.WhenAll(Enumerable.Range(0, 12)
                .Select(_ => Task.Run(store.GetVolumeInfo))).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.All(parallelCalls, quota => Assert.Equal(last, quota));
            Assert.Equal(2, api.StorageCalls);
            Assert.True(store.TryGetByPath("\\", out var root));
            Assert.Same(store.Root, root);
        }
        finally
        {
            release.Set();
        }

        Assert.Equal(50UL, (await refresh.WaitAsync(TimeSpan.FromSeconds(5))).FreeSize);
        Assert.Equal(2, api.StorageCalls);
    }

    public void Dispose()
    {
        if (Directory.Exists(_cacheDirectory))
        {
            Directory.Delete(_cacheDirectory, recursive: true);
        }
    }

    private O2CloudFileStore CreateStore(StorageApiClient api) => new(
        api, new ReadCacheOptions(_cacheDirectory, 256 * 1024, 0, 0), _time);

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now = _now.Add(duration);
    }

    private sealed class StorageApiClient : IO2CloudApiClient
    {
        private int _storageCalls;
        public int StorageCalls => Volatile.Read(ref _storageCalls);
        public Func<O2StorageInfo> ReadStorage { get; set; } = () => new O2StorageInfo(40, 100, 60);
        public O2CloudItemDto GetRootFolder() => new("root-test", string.Empty, null, true, 0, null, null, null, null, null, null);
        public O2StorageInfo GetStorageInfo()
        {
            Interlocked.Increment(ref _storageCalls);
            return ReadStorage();
        }

        public IReadOnlyList<O2CloudItemDto> ListFolder(string folderId) => throw new NotSupportedException();
        public bool KeepSessionAlive() => throw new NotSupportedException();
        public O2ChangeSummary GetChangesSince(long from) => throw new NotSupportedException();
        public IReadOnlyList<O2CloudItemDto> ListTrash() => throw new NotSupportedException();
        public byte[] DownloadFile(O2CloudItemDto item, ulong offset, uint length) => throw new NotSupportedException();
        public string GetShareLink(O2CloudItemDto item) => throw new NotSupportedException();
        public O2CloudItemDto CreateFolder(string parentFolderId, string name) => throw new NotSupportedException();
        public O2CloudItemDto UploadFile(string parentFolderId, string name, byte[] content) => throw new NotSupportedException();
        public O2CloudItemDto UploadFile(string parentFolderId, string name, Stream content, long contentLength) => throw new NotSupportedException();
        public O2CloudItemDto ReplaceFile(O2CloudItemDto previous, string parentFolderId, string name, Stream content, long contentLength) => throw new NotSupportedException();
        public void RenameOrMove(O2CloudItemDto item, string newName, string parentFolderId) => throw new NotSupportedException();
        public void MoveToTrash(O2CloudItemDto item) => throw new NotSupportedException();
        public O2CloudItemDto RestoreFromTrash(O2CloudItemDto item) => throw new NotSupportedException();
        public void PermanentlyDelete(O2CloudItemDto item) => throw new NotSupportedException();
    }
}
