using System.Net;
using O2CloudDrive.Api;
using O2CloudDrive.Auth;
using O2CloudDrive.VirtualFileSystem;

namespace O2CloudDrive.Tests;

public sealed class O2CloudFileStoreAuthTests
{
    [Fact]
    public void GetChildren_PropagatesAuthenticationRejection()
    {
        var cacheDirectory = Path.Combine(Path.GetTempPath(), "O2CloudDrive.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new O2CloudFileStore(
                new RejectingListApiClient(),
                new ReadCacheOptions(cacheDirectory, 256 * 1024, 0, 0));

            Assert.Throws<O2AuthenticationException>(() => store.GetChildren(store.Root));
        }
        finally
        {
            if (Directory.Exists(cacheDirectory))
            {
                Directory.Delete(cacheDirectory, recursive: true);
            }
        }
    }

    private sealed class RejectingListApiClient : IO2CloudApiClient
    {
        public O2CloudItemDto GetRootFolder() => new(
            "root-test", string.Empty, null, true, 0, null, null, null, null, null, null);

        public IReadOnlyList<O2CloudItemDto> ListFolder(string folderId) =>
            throw new O2AuthenticationException(HttpStatusCode.Unauthorized, "media/folder");

        public O2StorageInfo GetStorageInfo() => throw new NotSupportedException();
        public bool KeepSessionAlive() => throw new NotSupportedException();
        public O2ChangeSummary GetChangesSince(long from) => throw new NotSupportedException();
        public IReadOnlyList<O2CloudItemDto> ListTrash() => throw new NotSupportedException();
        public byte[] DownloadFile(O2CloudItemDto item, ulong offset, uint length) => throw new NotSupportedException();
        public string GetShareLink(O2CloudItemDto item) => throw new NotSupportedException();
        public O2CloudItemDto CreateFolder(string parentFolderId, string name) => throw new NotSupportedException();
        public O2CloudItemDto UploadFile(string parentFolderId, string name, byte[] content) => throw new NotSupportedException();
        public O2CloudItemDto UploadFile(string parentFolderId, string name, Stream content, long contentLength) => throw new NotSupportedException();
        public void RenameOrMove(O2CloudItemDto item, string newName, string parentFolderId) => throw new NotSupportedException();
        public void MoveToTrash(O2CloudItemDto item) => throw new NotSupportedException();
        public O2CloudItemDto RestoreFromTrash(O2CloudItemDto item) => throw new NotSupportedException();
        public void PermanentlyDelete(O2CloudItemDto item) => throw new NotSupportedException();
    }
}
