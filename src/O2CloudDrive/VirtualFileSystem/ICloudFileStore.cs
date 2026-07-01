namespace O2CloudDrive.VirtualFileSystem;

public interface ICloudFileStore
{
    CloudNode Root { get; }
    CloudVolumeInfo GetVolumeInfo();
    bool TryGetByPath(string path, out CloudNode node);
    bool TryGetChild(CloudNode directory, string name, out CloudNode child);
    IReadOnlyList<CloudNode> GetChildren(CloudNode directory);
    CloudNode Create(string path, CloudItemKind kind, uint attributes);
    void Delete(CloudNode node);
    void Rename(CloudNode node, string newPath, bool replaceIfExists);
    byte[] ReadAllBytes(CloudNode file);
    byte[] ReadBytes(CloudNode file, ulong offset, uint length);
    uint WriteBytes(CloudNode file, ulong offset, byte[] buffer, bool writeToEndOfFile, bool constrainedIo);
    void ReplaceContent(CloudNode file, byte[] content);
    void SetFileSize(CloudNode file, ulong size);
    void Commit(CloudNode node);
    void Touch(CloudNode node, bool updateWriteTime);
}

public sealed record CloudVolumeInfo(ulong TotalSize, ulong FreeSize);
