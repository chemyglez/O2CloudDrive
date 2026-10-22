using System.Collections.Concurrent;
using O2CloudDrive.Api;
using O2CloudDrive.VirtualFileSystem;

namespace O2CloudDrive.Tests;

public sealed class O2CloudFileStoreOverwriteTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public void Commit_WhenBackupTrashCleanupFails_KeepsConfirmedReplacementAsCurrentFile()
    {
        var version1 = ContentWithLength("CONTROL_VERSION_1", 133);
        var version2 = ContentWithLength("CONTROL_VERSION_2", 149);
        var api = new TransactionalFakeApiClient("overwrite-probe.txt", version1)
        {
            FailMoveToTrash = true,
        };
        var store = CreateStore(api);
        var file = Assert.Single(store.GetChildren(store.Root), node => !node.IsDirectory);

        store.ReplaceContent(file, version2);

        store.Commit(file);

        Assert.NotEqual("original-id", file.Id);
        Assert.Equal(149UL, file.Size);
        Assert.False(file.IsDirty);
        Assert.False(file.UploadInProgress);
        Assert.Equal(version2, store.ReadAllBytes(file));
        Assert.Equal(version1, api.RemoteContent("original-id"));
        Assert.EndsWith(".backup", api.RemoteFiles["original-id"].Name, StringComparison.OrdinalIgnoreCase);
        Assert.Single(api.RemoteFiles, remote =>
            remote.Value.Name.Equals("overwrite-probe.txt", StringComparison.OrdinalIgnoreCase) &&
            remote.Key.Equals(file.Id, StringComparison.OrdinalIgnoreCase) &&
            remote.Value.Content.LongLength == 149);
        Assert.DoesNotContain(api.RemoteFiles, remote =>
            remote.Value.Name.Equals("overwrite-probe (1).txt", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Commit_WhenReplacementFails_PreservesDirtyLocalVersionForRetry()
    {
        var version1 = ContentWithLength("CONTROL_VERSION_1", 133);
        var version2 = ContentWithLength("CONTROL_VERSION_2", 149);
        var api = new TransactionalFakeApiClient("overwrite-probe.txt", version1)
        {
            FailReplacement = true,
        };
        var store = CreateStore(api);
        var file = Assert.Single(store.GetChildren(store.Root), node => !node.IsDirectory);

        store.ReplaceContent(file, version2);

        Assert.Throws<IOException>(() => store.Commit(file));
        Assert.Equal("original-id", file.Id);
        Assert.Equal(149UL, file.Size);
        Assert.True(file.IsDirty);
        Assert.False(file.UploadInProgress);
        Assert.Equal(version2, store.ReadAllBytes(file));
        Assert.Equal(version1, api.RemoteContent("original-id"));

        var localContentPath = Assert.IsType<string>(file.LocalContentPath);
        Assert.True(File.Exists(localContentPath));
        Assert.True(File.Exists(localContentPath + ".pending.json"));

        store.Delete(file);
        Assert.False(File.Exists(localContentPath));
        Assert.False(File.Exists(localContentPath + ".pending.json"));
    }

    [Fact]
    public async Task SecondWrite_DuringUpload_WaitsAndThenCommitsBothCompleteVersions()
    {
        var version1 = ContentWithLength("CONTROL_VERSION_1", 133);
        var version2 = ContentWithLength("CONTROL_VERSION_2", 149);
        var api = new TransactionalFakeApiClient();
        var store = CreateStore(api);
        var file = store.Create("\\serial-write.txt", CloudItemKind.File, FileSystemAttributes.Archive);
        store.ReplaceContent(file, version1);
        api.BlockNextUpload();

        var firstCommit = Task.Run(() => store.Commit(file));
        Assert.True(api.UploadStarted.Wait(TestTimeout));

        var secondWriteAndCommit = Task.Run(() =>
        {
            store.ReplaceContent(file, version2);
            store.Commit(file);
        });

        try
        {
            await Task.Delay(100);
            Assert.False(secondWriteAndCommit.IsCompleted);
        }
        finally
        {
            api.ReleaseUpload();
        }

        await firstCommit.WaitAsync(TestTimeout);
        await secondWriteAndCommit.WaitAsync(TestTimeout);

        Assert.Collection(
            api.UploadedContents,
            content => Assert.Equal(version1, content),
            content => Assert.Equal(version2, content));
        Assert.False(file.IsDirty);
        Assert.False(file.UploadInProgress);
        Assert.Equal(version2, api.RemoteContent(file.Id));
    }

    [Fact]
    public async Task Rename_DuringUpload_WaitsAndThenRenamesUploadedObject()
    {
        var content = ContentWithLength("RENAME_DURING_UPLOAD", 149);
        var api = new TransactionalFakeApiClient();
        var store = CreateStore(api);
        var file = store.Create("\\before.txt", CloudItemKind.File, FileSystemAttributes.Archive);
        store.ReplaceContent(file, content);
        api.BlockNextUpload();

        var commit = Task.Run(() => store.Commit(file));
        Assert.True(api.UploadStarted.Wait(TestTimeout));

        var rename = Task.Run(() => store.Rename(file, "\\after.txt", replaceIfExists: false));
        try
        {
            await Task.Delay(100);
            Assert.False(rename.IsCompleted);
        }
        finally
        {
            api.ReleaseUpload();
        }

        await commit.WaitAsync(TestTimeout);
        await rename.WaitAsync(TestTimeout);

        Assert.False(store.TryGetByPath("\\before.txt", out _));
        Assert.True(store.TryGetByPath("\\after.txt", out var renamed));
        Assert.Same(file, renamed);
        Assert.Equal("after.txt", api.RemoteFiles[file.Id].Name);
        Assert.Contains(api.Renames, operation =>
            operation.Id == file.Id && operation.Name == "after.txt" && operation.ParentId == "root-test");
    }

    private static O2CloudFileStore CreateStore(TransactionalFakeApiClient api)
    {
        var cacheDirectory = Path.Combine(Path.GetTempPath(), "O2CloudDrive.Tests", Guid.NewGuid().ToString("N"));
        return new O2CloudFileStore(api, new ReadCacheOptions(cacheDirectory, 256 * 1024, 0, 0));
    }

    private static byte[] ContentWithLength(string marker, int length)
    {
        var prefix = System.Text.Encoding.UTF8.GetBytes(marker + "\n");
        var content = Enumerable.Repeat((byte)'x', length).ToArray();
        prefix.CopyTo(content, 0);
        return content;
    }

    private sealed class TransactionalFakeApiClient : IO2CloudApiClient
    {
        private readonly object _gate = new();
        private readonly ConcurrentQueue<byte[]> _uploadedContents = new();
        private int _nextId;
        private bool _blockNextUpload;

        public TransactionalFakeApiClient(string? initialName = null, byte[]? initialContent = null)
        {
            if (initialName is not null && initialContent is not null)
            {
                RemoteFiles["original-id"] = new RemoteFile(initialName, "root-test", initialContent.ToArray());
            }
        }

        public ManualResetEventSlim UploadStarted { get; } = new(false);
        public ManualResetEventSlim UploadRelease { get; } = new(false);
        public bool FailMoveToTrash { get; init; }
        public bool FailReplacement { get; init; }
        public ConcurrentDictionary<string, RemoteFile> RemoteFiles { get; } = new(StringComparer.OrdinalIgnoreCase);
        public ConcurrentQueue<(string Id, string Name, string ParentId)> Renames { get; } = new();
        public IReadOnlyList<byte[]> UploadedContents => _uploadedContents.ToArray();

        public void BlockNextUpload()
        {
            lock (_gate)
            {
                _blockNextUpload = true;
                UploadStarted.Reset();
                UploadRelease.Reset();
            }
        }

        public void ReleaseUpload() => UploadRelease.Set();

        public byte[] RemoteContent(string id) => RemoteFiles[id].Content.ToArray();

        public O2CloudItemDto GetRootFolder() => Item("root-test", string.Empty, null, isFolder: true, []);

        public IReadOnlyList<O2CloudItemDto> ListFolder(string folderId) => RemoteFiles
            .Select(pair => Item(pair.Key, pair.Value.Name, pair.Value.ParentId, isFolder: false, pair.Value.Content))
            .ToList();

        public O2StorageInfo GetStorageInfo() => new(0, 10L * 1024 * 1024 * 1024, 10L * 1024 * 1024 * 1024);
        public bool KeepSessionAlive() => true;
        public O2ChangeSummary GetChangesSince(long from) => new(false, from, new HashSet<string>(), new HashSet<string>(), false);
        public IReadOnlyList<O2CloudItemDto> ListTrash() => [];

        public byte[] DownloadFile(O2CloudItemDto item, ulong offset, uint length)
        {
            var content = RemoteContent(item.Id);
            if (offset >= (ulong)content.Length)
            {
                return [];
            }

            var count = (int)Math.Min(length, (ulong)content.Length - offset);
            return content.AsSpan(checked((int)offset), count).ToArray();
        }

        public string GetShareLink(O2CloudItemDto item) => throw new NotSupportedException();

        public O2CloudItemDto CreateFolder(string parentFolderId, string name) =>
            throw new NotSupportedException();

        public O2CloudItemDto UploadFile(string parentFolderId, string name, byte[] content)
        {
            using var stream = new MemoryStream(content, writable: false);
            return UploadFile(parentFolderId, name, stream, content.LongLength);
        }

        public O2CloudItemDto UploadFile(string parentFolderId, string name, Stream content, long contentLength) =>
            StoreUpload(parentFolderId, name, content, contentLength);

        public O2CloudItemDto ReplaceFile(
            O2CloudItemDto previous,
            string parentFolderId,
            string name,
            Stream content,
            long contentLength)
        {
            if (FailReplacement)
            {
                throw new IOException("Injected replacement failure.");
            }

            var uploaded = StoreUpload(parentFolderId, name + " (1)", content, contentLength);
            RenameOrMove(previous, name + ".backup", parentFolderId);
            RenameOrMove(uploaded, name, parentFolderId);
            try
            {
                MoveToTrash(previous with { Name = name + ".backup" });
            }
            catch (IOException)
            {
                // The replacement is already committed. Cleanup failure must not restore stale content.
            }

            return uploaded with { Name = name };
        }

        public void RenameOrMove(O2CloudItemDto item, string newName, string parentFolderId)
        {
            if (!RemoteFiles.TryGetValue(item.Id, out var remote))
            {
                throw new IOException("Remote item does not exist.");
            }

            RemoteFiles[item.Id] = remote with { Name = newName, ParentId = parentFolderId };
            Renames.Enqueue((item.Id, newName, parentFolderId));
        }

        public void MoveToTrash(O2CloudItemDto item)
        {
            if (FailMoveToTrash)
            {
                throw new IOException("Injected trash failure.");
            }

            RemoteFiles.TryRemove(item.Id, out _);
        }

        public O2CloudItemDto RestoreFromTrash(O2CloudItemDto item) => throw new NotSupportedException();
        public void PermanentlyDelete(O2CloudItemDto item) => RemoteFiles.TryRemove(item.Id, out _);

        private O2CloudItemDto StoreUpload(
            string parentFolderId,
            string name,
            Stream content,
            long contentLength)
        {
            bool shouldBlock;
            lock (_gate)
            {
                shouldBlock = _blockNextUpload;
                _blockNextUpload = false;
            }

            if (shouldBlock)
            {
                UploadStarted.Set();
                if (!UploadRelease.Wait(TestTimeout))
                {
                    throw new TimeoutException("The test did not release the blocked upload.");
                }
            }

            using var buffer = new MemoryStream();
            content.CopyTo(buffer);
            var bytes = buffer.ToArray();
            Assert.Equal(contentLength, bytes.LongLength);
            _uploadedContents.Enqueue(bytes);
            var id = "uploaded-" + Interlocked.Increment(ref _nextId);
            RemoteFiles[id] = new RemoteFile(name, parentFolderId, bytes);
            return Item(id, name, parentFolderId, isFolder: false, bytes);
        }

        private static O2CloudItemDto Item(
            string id,
            string name,
            string? parentId,
            bool isFolder,
            byte[] content) => new(
                id,
                name,
                parentId,
                isFolder,
                content.LongLength,
                DateTimeOffset.UtcNow,
                null,
                "file",
                null,
                null,
                null);

        public sealed record RemoteFile(string Name, string ParentId, byte[] Content);
    }
}

