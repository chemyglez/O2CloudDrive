using O2CloudDrive.Api;
using O2CloudDrive.VirtualFileSystem;

namespace O2CloudDrive.Tests;

public sealed class O2CloudFileStoreWriteCacheTests
{
    [Fact]
    public void Commit_KeepsConfirmedBytesForSubsequentReadAndPartialEdit()
    {
        using var test = new TestContext();
        var expected = test.Api.Content.ToArray();
        expected[10] = (byte)'Z';
        test.Store.WriteBytes(test.File, 10, [(byte)'Z'], false, false);
        test.Store.Commit(test.File);

        var cachedPath = Assert.IsType<string>(test.File.LocalContentPath);
        Assert.True(File.Exists(cachedPath));
        Assert.False(File.Exists(cachedPath + ".pending.json"));
        Assert.Equal(expected, test.Store.ReadAllBytes(test.File));
        Assert.Equal(1, test.Api.DownloadCount);

        expected[11] = (byte)'Y';
        test.Store.WriteBytes(test.File, 11, [(byte)'Y'], false, false);
        test.Store.Commit(test.File);

        Assert.Equal(expected, test.Api.Content);
        Assert.Equal(expected, test.Store.ReadAllBytes(test.File));
        Assert.Equal(1, test.Api.DownloadCount);
        Assert.Equal(2, test.Api.UploadCount);
    }

    [Fact]
    public void Commit_UsesOriginalRemoteSizeAfterLocalContentGrows()
    {
        using var test = new TestContext();
        test.Store.ReplaceContent(test.File, new byte[149]);
        test.Store.Commit(test.File);

        var previous = Assert.IsType<O2CloudItemDto>(test.Api.LastReplacedItem);
        Assert.Equal(133, previous.Size);
        Assert.Equal("original-id", previous.Id);
        Assert.Equal(149, test.Api.Content.Length);
        Assert.Equal(0, test.Api.DownloadCount);
    }

    [Fact]
    public void WriteBytes_WhenItCoversCompleteFile_DoesNotDownloadOriginal()
    {
        using var test = new TestContext();
        var replacement = Enumerable.Repeat((byte)'R', 149).ToArray();

        Assert.Equal(149U, test.Store.WriteBytes(test.File, 0, replacement, false, false));
        test.Store.Commit(test.File);

        Assert.Equal(replacement, test.Api.Content);
        Assert.Equal(0, test.Api.DownloadCount);
    }

    [Fact]
    public void SetFileSize_WhenTruncatingToZero_DoesNotDownloadOriginal()
    {
        using var test = new TestContext();
        test.Store.SetFileSize(test.File, 0);
        test.Store.Commit(test.File);

        Assert.Empty(test.Api.Content);
        Assert.Equal(0UL, test.File.Size);
        Assert.Equal(0, test.Api.DownloadCount);
        Assert.Equal(1, test.Api.UploadCount);
    }

    [Fact]
    public void UnchangedSizeAndEmptyOrConstrainedWrites_DoNotDownloadOrUpload()
    {
        using var test = new TestContext();
        test.Store.SetFileSize(test.File, test.File.Size);
        Assert.Equal(0U, test.Store.WriteBytes(test.File, 0, [], false, false));
        Assert.Equal(0U, test.Store.WriteBytes(test.File, 9999, [(byte)'x'], false, true));
        Assert.Equal(0U, test.Store.WriteBytes(test.File, 0, [(byte)'x'], true, true));
        test.Store.Commit(test.File);

        Assert.False(test.File.IsDirty);
        Assert.Equal(133UL, test.File.Size);
        Assert.Equal(0, test.Api.DownloadCount);
        Assert.Equal(0, test.Api.UploadCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IncompleteHydration_DoesNotPermitPartialEditOrSilentlyTruncate(bool readOnly)
    {
        using var test = new TestContext();
        test.Api.ShortDownload = true;
        var original = test.Api.Content.ToArray();

        if (readOnly)
        {
            Assert.Throws<IOException>(() => test.Store.ReadAllBytes(test.File));
        }
        else
        {
            Assert.Throws<IOException>(() => test.Store.WriteBytes(test.File, 10, [(byte)'x'], false, false));
        }

        Assert.False(test.File.IsDirty);
        Assert.Equal(133UL, test.File.Size);
        Assert.Null(test.File.LocalContentPath);
        test.Store.Commit(test.File);
        Assert.Equal(original, test.Api.Content);
        Assert.Equal(0, test.Api.UploadCount);
    }

    [Fact]
    public void LargePartialEdit_HydratesInBoundedChunksAndPreservesUntouchedBytes()
    {
        using var test = new TestContext(20 * 1024 * 1024);
        var expected = test.Api.Content.ToArray();
        expected[^1] = (byte)'Z';
        test.Store.WriteBytes(test.File, (ulong)expected.Length - 1, [(byte)'Z'], false, false);
        test.Store.Commit(test.File);

        Assert.Equal(expected, test.Api.Content);
        Assert.Equal(3, test.Api.DownloadCount);
        Assert.All(test.Api.DownloadLengths, length => Assert.InRange(length, 1U, 8U * 1024 * 1024));
        Assert.Null(test.File.LocalContentPath);
    }

    [Fact]
    public void LargePartialEdit_WhenSecondHydrationChunkIsShort_PreservesRemoteSizeAndContent()
    {
        using var test = new TestContext(20 * 1024 * 1024);
        var original = test.Api.Content.ToArray();
        test.Api.ShortDownloadNumber = 2;

        Assert.Throws<IOException>(() => test.Store.WriteBytes(test.File, 10, [(byte)'Z'], false, false));

        Assert.Equal(2, test.Api.DownloadCount);
        Assert.Equal((ulong)original.Length, test.File.Size);
        Assert.False(test.File.IsDirty);
        Assert.Null(test.File.LocalContentPath);
        test.Store.Commit(test.File);
        Assert.Equal(original, test.Api.Content);
        Assert.Equal(0, test.Api.UploadCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConfirmedLocalCache_IsBoundedAndNeverEvictsDirtyContent(bool keepFirstDirty)
    {
        using var test = new TestContext();
        test.Store.ReplaceContent(test.File, new byte[133]);
        test.Store.Commit(test.File);
        var firstPath = Assert.IsType<string>(test.File.LocalContentPath);
        if (keepFirstDirty)
        {
            test.Store.WriteBytes(test.File, 0, [(byte)'Z'], false, false);
        }

        for (var index = 0; index < 16; index++)
        {
            var file = test.Store.Create($"\\retained-{index}.txt", CloudItemKind.File, FileSystemAttributes.Archive);
            test.Store.ReplaceContent(file, [(byte)'a']);
            test.Store.Commit(file);
        }

        Assert.Equal(keepFirstDirty, File.Exists(firstPath));
        Assert.Equal(keepFirstDirty, test.File.IsDirty);
        if (keepFirstDirty)
        {
            Assert.Equal((byte)'Z', test.Store.ReadAllBytes(test.File)[0]);
        }
        else
        {
            Assert.Null(test.File.LocalContentPath);
        }
    }

    private sealed class TestContext : IDisposable
    {
        private readonly string _cacheDirectory = Path.Combine(Path.GetTempPath(), "O2CloudDrive.Tests", Guid.NewGuid().ToString("N"));

        public TestContext(int contentLength = 133)
        {
            Api = new WriteCacheApi(contentLength);
            Store = new O2CloudFileStore(Api, new ReadCacheOptions(_cacheDirectory, 256 * 1024, 0, 0));
            File = Assert.Single(Store.GetChildren(Store.Root), node => !node.IsDirectory);
        }

        public WriteCacheApi Api { get; }
        public O2CloudFileStore Store { get; }
        public CloudNode File { get; }

        public void Dispose()
        {
            foreach (var file in Store.GetChildren(Store.Root).Where(node => !node.IsDirectory))
            {
                Store.Delete(file);
            }
            if (Directory.Exists(_cacheDirectory))
            {
                Directory.Delete(_cacheDirectory, recursive: true);
            }
        }
    }

    private sealed class WriteCacheApi : IO2CloudApiClient
    {
        private string _id = "original-id";
        public WriteCacheApi(int contentLength) => Content = Enumerable.Repeat((byte)'a', contentLength).ToArray();

        public byte[] Content { get; private set; }
        public int DownloadCount { get; private set; }
        public List<uint> DownloadLengths { get; } = [];
        public int UploadCount { get; private set; }
        public bool ShortDownload { get; set; }
        public int ShortDownloadNumber { get; set; }
        public O2CloudItemDto? LastReplacedItem { get; private set; }

        public O2CloudItemDto GetRootFolder() => new("root", string.Empty, null, true, 0, null, null, null, null, null, null);
        public IReadOnlyList<O2CloudItemDto> ListFolder(string folderId) => [Item()];
        public O2StorageInfo GetStorageInfo() => throw new NotSupportedException();
        public bool KeepSessionAlive() => true;
        public O2ChangeSummary GetChangesSince(long from) => new(false, from, new HashSet<string>(), new HashSet<string>(), false);
        public IReadOnlyList<O2CloudItemDto> ListTrash() => [];

        public byte[] DownloadFile(O2CloudItemDto item, ulong offset, uint length)
        {
            DownloadCount++;
            DownloadLengths.Add(length);
            var available = (int)Math.Min(length, (ulong)Content.Length - offset);
            var shortDownload = ShortDownload || ShortDownloadNumber == DownloadCount;
            return Content.AsSpan((int)offset, shortDownload ? Math.Max(0, available - 1) : available).ToArray();
        }

        public O2CloudItemDto ReplaceFile(O2CloudItemDto previous, string parentFolderId, string name, Stream content, long contentLength)
        {
            LastReplacedItem = previous;
            return UploadFile(parentFolderId, name, content, contentLength);
        }

        public O2CloudItemDto UploadFile(string parentFolderId, string name, Stream content, long contentLength)
        {
            using var buffer = new MemoryStream();
            content.CopyTo(buffer);
            Content = buffer.ToArray();
            Assert.Equal(contentLength, Content.LongLength);
            _id = "uploaded-" + ++UploadCount;
            return Item();
        }

        public O2CloudItemDto UploadFile(string parentFolderId, string name, byte[] content) => throw new NotSupportedException();
        public string GetShareLink(O2CloudItemDto item) => throw new NotSupportedException();
        public O2CloudItemDto CreateFolder(string parentFolderId, string name) => throw new NotSupportedException();
        public void RenameOrMove(O2CloudItemDto item, string newName, string parentFolderId) => throw new NotSupportedException();
        public void MoveToTrash(O2CloudItemDto item) { }
        public O2CloudItemDto RestoreFromTrash(O2CloudItemDto item) => throw new NotSupportedException();
        public void PermanentlyDelete(O2CloudItemDto item) { }

        private O2CloudItemDto Item() => new(_id, "editable.txt", "root", false, Content.LongLength, DateTimeOffset.UnixEpoch, null, "file", _id, null, null);
    }
}
