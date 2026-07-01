namespace O2CloudDrive.VirtualFileSystem;

public sealed class CloudNode
{
    internal CloudNode(string id, string name, CloudItemKind kind, CloudNode? parent, ulong indexNumber)
    {
        Id = id;
        Name = name;
        Kind = kind;
        Parent = parent;
        IndexNumber = indexNumber;

        var now = DateTimeOffset.UtcNow.ToFileTime();
        CreationTime = (ulong)now;
        LastAccessTime = (ulong)now;
        LastWriteTime = (ulong)now;
        ChangeTime = (ulong)now;
        Attributes = kind == CloudItemKind.Directory ? FileSystemAttributes.Directory : FileSystemAttributes.Archive;
    }

    public string Id { get; internal set; }
    public string Name { get; internal set; }
    public CloudItemKind Kind { get; }
    public CloudNode? Parent { get; internal set; }
    public ulong IndexNumber { get; }
    public uint Attributes { get; internal set; }
    public ulong CreationTime { get; internal set; }
    public ulong LastAccessTime { get; internal set; }
    public ulong LastWriteTime { get; internal set; }
    public ulong ChangeTime { get; internal set; }
    public byte[] Content { get; internal set; } = [];
    public string? LocalContentPath { get; internal set; }
    public ulong? DeclaredSize { get; internal set; }
    public string? DirectUrl { get; internal set; }
    public string? MediaKind { get; internal set; }
    public string? Fingerprint { get; internal set; }
    public string? Node { get; internal set; }
    public string? DownloadToken { get; internal set; }
    public ulong? ExplicitFileSize { get; internal set; }
    public ulong HighestWrittenOffset { get; internal set; }
    public bool UploadPendingRemoteConfirmation { get; internal set; }
    public bool UploadInProgress { get; internal set; }
    public bool IsDirty { get; internal set; }
    public bool IsNew { get; internal set; }
    public bool IsVirtualTrashRoot { get; internal set; }
    public bool IsTrashItem { get; internal set; }
    public bool ChildrenLoaded { get; internal set; }
    public Dictionary<string, CloudNode> Children { get; } = new(StringComparer.OrdinalIgnoreCase);
    public bool IsDirectory => Kind == CloudItemKind.Directory;
    public ulong Size => IsDirectory ? 0UL : DeclaredSize ?? (ulong)Content.LongLength;

    public string FullPath
    {
        get
        {
            if (Parent is null)
            {
                return "\\";
            }

            var segments = new Stack<string>();
            var current = this;
            while (current.Parent is not null)
            {
                segments.Push(current.Name);
                current = current.Parent;
            }

            return "\\" + string.Join("\\", segments);
        }
    }
}
