using O2CloudDrive.Api;
using System.Security.Cryptography;
using System.Text;

namespace O2CloudDrive.VirtualFileSystem;

public sealed class O2CloudFileStore : ICloudFileStore
{
    private const ulong MaxEditableFileBytes = 3_900_000_000UL;
    private const uint DirectReadThresholdBytes = 256 * 1024;
    private const int MinReadCacheBlockSizeBytes = 256 * 1024;
    private const int MaxReadCacheBlockSizeBytes = 16 * 1024 * 1024;
    private const int MaxReadAheadBlocks = 8;
    private const string TrashNodeId = "virtual:o2-trash";
    private const string TrashNodeName = "Papelera";
    private static readonly TimeSpan RemoteChangesCheckInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ReadCachePruneInterval = TimeSpan.FromMinutes(10);
    private static readonly bool EnableRemoteChangesPolling = false;
    private static readonly string[] StreamableExtensions =
    [
        ".3gp",
        ".aac",
        ".avi",
        ".flac",
        ".m4a",
        ".m4v",
        ".mkv",
        ".mov",
        ".mp3",
        ".mp4",
        ".mpeg",
        ".mpg",
        ".ogg",
        ".wav",
        ".webm",
        ".wmv",
    ];
    private static readonly string BaseWriteCacheDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "O2CloudDrive",
        "WriteCache");
    private static readonly CloudVolumeInfo DefaultVolume = new(
        TotalSize: 10UL * 1024UL * 1024UL * 1024UL * 1024UL,
        FreeSize: 10UL * 1024UL * 1024UL * 1024UL * 1024UL);
    private readonly object _gate = new();
    private readonly object _readCacheGate = new();
    private readonly IO2CloudApiClient _apiClient;
    private readonly string _localContentDirectory;
    private readonly string _readCacheDirectory;
    private readonly int _readCacheBlockSizeBytes;
    private readonly int _readAheadBlocks;
    private readonly long _readCacheMaxBytes;
    private readonly Dictionary<string, SemaphoreSlim> _readBlockLocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _readAheadInFlight = new(StringComparer.OrdinalIgnoreCase);
    private ulong _nextIndex = 1;
    private CloudVolumeInfo _lastVolume = DefaultVolume;
    private long _remoteChangesCursor = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    private DateTimeOffset _nextRemoteChangesCheckAt = DateTimeOffset.UtcNow.Add(RemoteChangesCheckInterval);
    private DateTimeOffset _nextReadCachePruneAt = DateTimeOffset.UtcNow.Add(ReadCachePruneInterval);
    private bool _remoteChangesCheckInProgress;

    public O2CloudFileStore(IO2CloudApiClient apiClient)
        : this(apiClient, ReadCacheOptions.Default)
    {
    }

    public O2CloudFileStore(IO2CloudApiClient apiClient, ReadCacheOptions readCacheOptions)
    {
        _apiClient = apiClient;
        _readCacheDirectory = Path.Combine(readCacheOptions.CacheDirectory, "ReadBlocks");
        _readCacheBlockSizeBytes = Math.Clamp(readCacheOptions.BlockSizeBytes, MinReadCacheBlockSizeBytes, MaxReadCacheBlockSizeBytes);
        _readAheadBlocks = Math.Clamp(readCacheOptions.ReadAheadBlocks, 0, MaxReadAheadBlocks);
        _readCacheMaxBytes = Math.Max(0, readCacheOptions.MaxBytes);
        PurgeStaleWriteCaches();
        _localContentDirectory = Path.Combine(BaseWriteCacheDirectory, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_localContentDirectory);
        Directory.CreateDirectory(_readCacheDirectory);
        var root = _apiClient.GetRootFolder();
        Root = NewNode(root.Id, string.Empty, CloudItemKind.Directory, null);
        ApplyMetadata(Root, root);
    }

    public CloudNode Root { get; }

    public CloudVolumeInfo GetVolumeInfo()
    {
        try
        {
            var storage = _apiClient.GetStorageInfo();
            var total = checked((ulong)Math.Max(0, storage.TotalBytes));
            var free = checked((ulong)Math.Clamp(storage.FreeBytes, 0, storage.TotalBytes));
            var volume = new CloudVolumeInfo(total, free);
            lock (_gate)
            {
                _lastVolume = volume;
            }

            return volume;
        }
        catch
        {
            lock (_gate)
            {
                return _lastVolume;
            }
        }
    }

    public bool TryGetByPath(string path, out CloudNode node)
    {
        lock (_gate)
        {
            RefreshForRemoteChangesIfNeeded();
            return TryFindByPath(path, out node!);
        }
    }

    public bool TryGetChild(CloudNode directory, string name, out CloudNode child)
    {
        lock (_gate)
        {
            child = null!;
            if (!directory.IsDirectory)
            {
                return false;
            }

            EnsureChildrenLoaded(directory);
            return directory.Children.TryGetValue(name, out child!);
        }
    }

    public IReadOnlyList<CloudNode> GetChildren(CloudNode directory)
    {
        lock (_gate)
        {
            EnsureDirectory(directory);
            EnsureChildrenLoaded(directory);
            return directory.Children.Values
                .OrderBy(node => node.IsDirectory ? 0 : 1)
                .ThenBy(node => node.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    public CloudNode Create(string path, CloudItemKind kind, uint attributes)
    {
        lock (_gate)
        {
            var normalized = PathTools.Normalize(path);
            if (normalized == "\\")
            {
                throw new InvalidOperationException("Root already exists.");
            }

            if (TryFindByPath(normalized, out _))
            {
                throw new IOException("The target already exists.");
            }

            var parent = GetRequiredParent(normalized);
            EnsureChildrenLoaded(parent);
            if (parent.IsVirtualTrashRoot || parent.IsTrashItem)
            {
                throw new UnauthorizedAccessException("No se pueden crear elementos dentro de la papelera.");
            }

            var name = PathTools.GetName(normalized);

            if (kind == CloudItemKind.Directory)
            {
                var created = _apiClient.CreateFolder(parent.Id, name);
                MarkRemoteChangeHandled();
                var nodeName = UniqueName(SanitizeName(created.Name), created.Id, parent.Children.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase));
                var node = NewNode(created.Id, nodeName, CloudItemKind.Directory, parent);
                ApplyMetadata(node, created);
                parent.Children.Add(node.Name, node);
                Touch(parent, updateWriteTime: true);
                return node;
            }

            var file = NewNode("local:" + Guid.NewGuid().ToString("N"), name, CloudItemKind.File, parent);
            file.Attributes = NormalizeFileAttributes(attributes);
            file.DeclaredSize = 0;
            File.WriteAllBytes(EnsureLocalContentPath(file), []);
            file.IsNew = true;
            file.IsDirty = true;
            file.ExplicitFileSize = null;
            file.HighestWrittenOffset = 0;
            file.UploadPendingRemoteConfirmation = false;
            parent.Children.Add(file.Name, file);
            Touch(parent, updateWriteTime: true);
            return file;
        }
    }

    public void Delete(CloudNode node)
    {
        lock (_gate)
        {
            if (node.Parent is null)
            {
                throw new InvalidOperationException("The root directory cannot be deleted.");
            }

            if (node.UploadInProgress)
            {
                throw new IOException("No se puede eliminar un archivo mientras se esta subiendo a O2 Cloud.");
            }

            if (node.IsVirtualTrashRoot)
            {
                throw new UnauthorizedAccessException("La papelera virtual no se puede eliminar.");
            }

            if (node.IsDirectory && !node.IsTrashItem)
            {
                EnsureChildrenLoaded(node);
                if (node.Children.Count > 0)
                {
                    throw new IOException("The directory is not empty.");
                }
            }

            if (!node.IsNew || node.UploadPendingRemoteConfirmation)
            {
                if (node.IsTrashItem)
                {
                    _apiClient.PermanentlyDelete(ToDto(node));
                }
                else
                {
                    _apiClient.MoveToTrash(ToDto(node));
                }
                MarkRemoteChangeHandled();
            }

            node.Parent.Children.Remove(node.Name);
            DeleteLocalContent(node);
            Touch(node.Parent, updateWriteTime: true);
        }
    }

    public void Rename(CloudNode node, string newPath, bool replaceIfExists)
    {
        lock (_gate)
        {
            if (node.Parent is null)
            {
                throw new InvalidOperationException("The root directory cannot be renamed.");
            }

            if (node.UploadInProgress)
            {
                throw new IOException("No se puede renombrar o mover un archivo mientras se esta subiendo a O2 Cloud.");
            }

            var normalized = PathTools.Normalize(newPath);
            var newParent = GetRequiredParent(normalized);
            EnsureChildrenLoaded(newParent);
            if (node.IsVirtualTrashRoot || newParent.IsVirtualTrashRoot || newParent.IsTrashItem)
            {
                throw new UnauthorizedAccessException("La papelera no admite renombrar o mover elementos.");
            }

            if (node.IsDirectory && IsSameOrDescendant(newParent, node))
            {
                throw new InvalidOperationException("A directory cannot be moved into itself.");
            }

            var oldParent = node.Parent;
            var newName = PathTools.GetName(normalized);
            if (ReferenceEquals(oldParent, newParent) &&
                node.Name.Equals(newName, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (newParent.Children.TryGetValue(newName, out var existing) && !ReferenceEquals(existing, node))
            {
                if (!replaceIfExists)
                {
                    throw new IOException("The target already exists.");
                }

                Delete(existing);
            }

            if (node.IsTrashItem)
            {
                var restored = _apiClient.RestoreFromTrash(ToDto(node));
                var restoredDto = restored with { Name = newName, ParentId = newParent.Id };
                _apiClient.RenameOrMove(restoredDto, newName, newParent.Id);

                oldParent.Children.Remove(node.Name);
                node.Parent = newParent;
                node.Name = newName;
                node.IsTrashItem = false;
                ApplyMetadata(node, restoredDto);
                newParent.Children[newName] = node;
                MarkRemoteChangeHandled();
                Touch(node, updateWriteTime: false);
                Touch(oldParent, updateWriteTime: true);
                if (!ReferenceEquals(oldParent, newParent))
                {
                    Touch(newParent, updateWriteTime: true);
                }

                return;
            }

            if (!node.IsNew)
            {
                _apiClient.RenameOrMove(ToDto(node), newName, newParent.Id);
                MarkRemoteChangeHandled();
            }

            oldParent.Children.Remove(node.Name);
            node.Parent = newParent;
            node.Name = newName;
            newParent.Children[newName] = node;
            Touch(node, updateWriteTime: false);
            Touch(oldParent, updateWriteTime: true);
            if (!ReferenceEquals(oldParent, newParent))
            {
                Touch(newParent, updateWriteTime: true);
            }
        }
    }

    public byte[] ReadAllBytes(CloudNode file)
    {
        lock (_gate)
        {
            EnsureFile(file);
            HydrateFile(file);
            Touch(file, updateWriteTime: false);
            return HasLocalContent(file)
                ? File.ReadAllBytes(file.LocalContentPath!)
                : file.Content.ToArray();
        }
    }

    public byte[] ReadBytes(CloudNode file, ulong offset, uint length)
    {
        O2CloudItemDto remoteItem;
        uint boundedLength;
        ulong remoteSize;
        lock (_gate)
        {
            EnsureFile(file);
            if (length == 0 || offset >= file.Size)
            {
                return [];
            }

            if (file.IsDirty || file.IsNew || HasCompleteLocalContent(file))
            {
                var localBytes = ReadLocalBytes(file, offset, length);
                Touch(file, updateWriteTime: false);
                return localBytes;
            }

            boundedLength = (uint)Math.Min(length, file.Size - offset);
            remoteSize = file.Size;
            remoteItem = ToDto(file);
        }

        byte[] bytes;
        try
        {
            bytes = ReadRemoteBytesWithCache(remoteItem, offset, boundedLength, remoteSize);
            if ((uint)bytes.Length < boundedLength &&
                offset + (ulong)bytes.Length < remoteSize)
            {
                throw new IOException("O2 Cloud devolvio una lectura incompleta antes del final del archivo.");
            }
        }
        catch (IOException) when (CanHydrateForReadFallback(remoteSize))
        {
            lock (_gate)
            {
                HydrateFile(file);
                var localBytes = ReadLocalBytes(file, offset, boundedLength);
                Touch(file, updateWriteTime: false);
                return localBytes;
            }
        }

        lock (_gate)
        {
            Touch(file, updateWriteTime: false);
            return bytes;
        }
    }

    public uint WriteBytes(CloudNode file, ulong offset, byte[] buffer, bool writeToEndOfFile, bool constrainedIo)
    {
        lock (_gate)
        {
            EnsureFile(file);
            HydrateFile(file);
            var currentLength = LocalContentLength(file);
            var writeOffset = writeToEndOfFile ? currentLength : offset;
            if (constrainedIo && writeOffset >= currentLength)
            {
                return 0;
            }

            var writable = constrainedIo
                ? (uint)Math.Min((ulong)buffer.Length, currentLength - writeOffset)
                : (uint)buffer.Length;

            var requiredLength = checked(writeOffset + writable);
            EnsureEditableLength(requiredLength);
            var path = EnsureLocalContentPath(file);
            using (var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read))
            {
                if (requiredLength > (ulong)stream.Length)
                {
                    stream.SetLength(checked((long)requiredLength));
                }

                stream.Position = checked((long)writeOffset);
                stream.Write(buffer, 0, checked((int)writable));
            }

            file.Content = [];
            file.DeclaredSize = LocalContentLength(file);
            file.HighestWrittenOffset = Math.Max(file.HighestWrittenOffset, writeOffset + writable);
            file.IsDirty = true;
            Touch(file, updateWriteTime: true);

            return writable;
        }
    }

    public void ReplaceContent(CloudNode file, byte[] content)
    {
        lock (_gate)
        {
            EnsureFile(file);
            EnsureEditableLength((ulong)content.LongLength);
            File.WriteAllBytes(EnsureLocalContentPath(file), content);
            file.Content = [];
            file.DeclaredSize = (ulong)content.LongLength;
            file.ExplicitFileSize = (ulong)content.LongLength;
            file.HighestWrittenOffset = (ulong)content.LongLength;
            file.IsDirty = true;
            Touch(file, updateWriteTime: true);
        }
    }

    public void SetFileSize(CloudNode file, ulong size)
    {
        lock (_gate)
        {
            EnsureFile(file);
            EnsureEditableLength(size);
            HydrateFile(file);
            using (var stream = new FileStream(EnsureLocalContentPath(file), FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read))
            {
                stream.SetLength(checked((long)size));
            }

            file.Content = [];
            file.DeclaredSize = size;
            file.ExplicitFileSize = size;
            file.HighestWrittenOffset = Math.Min(file.HighestWrittenOffset, size);
            file.IsDirty = true;
            Touch(file, updateWriteTime: true);
        }
    }

    public void Commit(CloudNode node)
    {
        O2CloudItemDto? previous;
        string parentId;
        string name;
        long size;
        string? contentPath;
        byte[]? contentBytes;
        bool wasNew;

        lock (_gate)
        {
            if (node.IsDirectory)
            {
                node.IsDirty = false;
                node.IsNew = false;
                return;
            }

            if (!node.IsDirty && !node.IsNew)
            {
                return;
            }

            if (node.UploadInProgress)
            {
                return;
            }

            if (node.Parent is null)
            {
                throw new InvalidOperationException("The root directory cannot be uploaded.");
            }

            wasNew = node.IsNew;
            previous = wasNew ? null : ToDto(node);
            parentId = node.Parent.Id;
            name = node.Name;
            size = checked((long)node.Size);
            contentPath = HasLocalContent(node) ? node.LocalContentPath : null;
            contentBytes = contentPath is null ? node.Content.ToArray() : null;
            node.UploadInProgress = true;
            node.UploadPendingRemoteConfirmation = true;
            node.IsDirty = false;
        }

        _ = Task.Run(() => UploadCommittedFile(node, previous, parentId, name, size, contentPath, contentBytes, wasNew));
    }

    private void UploadCommittedFile(
        CloudNode node,
        O2CloudItemDto? previous,
        string parentId,
        string name,
        long size,
        string? contentPath,
        byte[]? contentBytes,
        bool wasNew)
    {
        try
        {
            using var content = OpenCommittedContentForRead(contentPath, contentBytes);
            var uploaded = _apiClient.UploadFile(parentId, name, content, size);
            if (previous is not null &&
                !previous.Id.Equals(uploaded.Id, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    _apiClient.MoveToTrash(previous);
                }
                catch
                {
                    // If cleanup of the previous remote object fails, keep the uploaded file visible.
                }
            }

            lock (_gate)
            {
                ApplyMetadata(node, uploaded);
                node.DeclaredSize = uploaded.Size > 0 ? (ulong)uploaded.Size : node.Size;
                node.UploadPendingRemoteConfirmation = IsPendingUploadId(uploaded.Id);
                node.UploadInProgress = false;
                if (!node.UploadPendingRemoteConfirmation)
                {
                    DeleteLocalContent(node);
                }

                node.ExplicitFileSize = null;
                node.HighestWrittenOffset = 0;
                node.IsDirty = false;
                node.IsNew = false;
                MarkRemoteChangeHandled();
            }
        }
        catch
        {
            lock (_gate)
            {
                node.UploadInProgress = false;
                node.UploadPendingRemoteConfirmation = false;
                node.IsDirty = true;
                node.IsNew = wasNew;
            }
        }
    }

    public void Touch(CloudNode node, bool updateWriteTime)
    {
        var now = (ulong)DateTimeOffset.UtcNow.ToFileTime();
        node.LastAccessTime = now;
        node.ChangeTime = now;
        if (updateWriteTime)
        {
            node.LastWriteTime = now;
        }
    }

    private void EnsureChildrenLoaded(CloudNode directory)
    {
        EnsureDirectory(directory);
        RefreshForRemoteChangesIfNeeded();
        if (directory.IsVirtualTrashRoot)
        {
            if (!directory.ChildrenLoaded)
            {
                LoadTrashChildren(directory);
            }

            return;
        }

        if (directory.ChildrenLoaded)
        {
            return;
        }

        IReadOnlyList<O2CloudItemDto> items;
        try
        {
            items = _apiClient.ListFolder(directory.Id);
        }
        catch (Exception ex)
        {
            LoadListingError(directory, ex);
            return;
        }

        directory.Children.Clear();
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (directory.Parent is null)
        {
            AddTrashRoot(directory, usedNames);
        }

        foreach (var item in items)
        {
            var name = UniqueName(SanitizeName(item.Name), item.Id, usedNames);
            var node = NewNode(item.Id, name, item.IsFolder ? CloudItemKind.Directory : CloudItemKind.File, directory);
            ApplyMetadata(node, item);
            directory.Children.Add(node.Name, node);
        }

        directory.ChildrenLoaded = true;
    }

    private void RefreshForRemoteChangesIfNeeded()
    {
        if (!EnableRemoteChangesPolling)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (now < _nextRemoteChangesCheckAt || _remoteChangesCheckInProgress)
        {
            return;
        }

        _nextRemoteChangesCheckAt = now.Add(RemoteChangesCheckInterval);
        if (HasActiveLocalChanges(Root))
        {
            return;
        }

        _remoteChangesCheckInProgress = true;
        var cursor = _remoteChangesCursor;
        _ = Task.Run(() => RefreshForRemoteChangesInBackground(cursor));
    }

    private void RefreshForRemoteChangesInBackground(long cursor)
    {
        O2ChangeSummary? changes = null;
        try
        {
            changes = _apiClient.GetChangesSince(cursor);
        }
        catch
        {
        }
        finally
        {
            lock (_gate)
            {
                try
                {
                    if (changes is not null)
                    {
                        _remoteChangesCursor = Math.Max(_remoteChangesCursor, changes.NextCursor);
                        if (changes.HasChanges && !HasActiveLocalChanges(Root))
                        {
                            if (!TryInvalidateChangedChildren(Root, changes))
                            {
                                InvalidateLoadedChildren(Root);
                            }
                        }
                    }
                }
                finally
                {
                    _remoteChangesCheckInProgress = false;
                }
            }
        }
    }

    private void MarkRemoteChangeHandled()
    {
        _remoteChangesCursor = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _nextRemoteChangesCheckAt = DateTimeOffset.UtcNow.Add(RemoteChangesCheckInterval);
    }

    private static bool HasActiveLocalChanges(CloudNode node)
    {
        if (node.IsDirty || node.IsNew || node.UploadInProgress || node.UploadPendingRemoteConfirmation)
        {
            return true;
        }

        return node.Children.Values.Any(HasActiveLocalChanges);
    }

    private static void InvalidateLoadedChildren(CloudNode node)
    {
        foreach (var child in node.Children.Values.ToList())
        {
            InvalidateLoadedChildren(child);
        }

        if (node.IsDirectory)
        {
            node.Children.Clear();
            node.ChildrenLoaded = false;
        }
    }

    private static bool TryInvalidateChangedChildren(CloudNode root, O2ChangeSummary changes)
    {
        if (changes.HasNewFileSystemItems)
        {
            return false;
        }

        var changedIds = new HashSet<string>(changes.FolderIds, StringComparer.OrdinalIgnoreCase);
        changedIds.UnionWith(changes.FileIds);
        if (changedIds.Count == 0)
        {
            return true;
        }

        var matchedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var parentsToInvalidate = new HashSet<CloudNode>();
        var directoriesToInvalidate = new HashSet<CloudNode>();
        CollectChangedNodes(root, changedIds, changes.FolderIds, matchedIds, parentsToInvalidate, directoriesToInvalidate);
        if (matchedIds.Count != changedIds.Count)
        {
            return false;
        }

        foreach (var directory in directoriesToInvalidate)
        {
            InvalidateLoadedChildren(directory);
        }

        foreach (var parent in parentsToInvalidate)
        {
            parent.Children.Clear();
            parent.ChildrenLoaded = false;
        }

        return true;
    }

    private static void CollectChangedNodes(
        CloudNode node,
        ISet<string> changedIds,
        IReadOnlySet<string> changedFolderIds,
        ISet<string> matchedIds,
        ISet<CloudNode> parentsToInvalidate,
        ISet<CloudNode> directoriesToInvalidate)
    {
        foreach (var child in node.Children.Values.ToList())
        {
            if (changedIds.Contains(child.Id))
            {
                matchedIds.Add(child.Id);
                if (child.Parent is not null)
                {
                    parentsToInvalidate.Add(child.Parent);
                }

                if (child.IsDirectory && changedFolderIds.Contains(child.Id))
                {
                    directoriesToInvalidate.Add(child);
                }
            }

            if (child.IsDirectory && child.ChildrenLoaded)
            {
                CollectChangedNodes(child, changedIds, changedFolderIds, matchedIds, parentsToInvalidate, directoriesToInvalidate);
            }
        }
    }

    private void AddTrashRoot(CloudNode root, ISet<string> usedNames)
    {
        var name = UniqueName(TrashNodeName, TrashNodeId, usedNames);
        var trash = NewNode(TrashNodeId, name, CloudItemKind.Directory, root);
        trash.IsVirtualTrashRoot = true;
        trash.Attributes = FileSystemAttributes.Directory;
        root.Children.Add(trash.Name, trash);
    }

    private void LoadTrashChildren(CloudNode trashRoot)
    {
        IReadOnlyList<O2CloudItemDto> items;
        try
        {
            items = _apiClient.ListTrash();
        }
        catch (Exception ex)
        {
            LoadListingError(trashRoot, ex);
            return;
        }

        trashRoot.Children.Clear();
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            var name = UniqueName(SanitizeName(item.Name), item.Id, usedNames);
            var node = NewNode(item.Id, name, item.IsFolder ? CloudItemKind.Directory : CloudItemKind.File, trashRoot);
            node.IsTrashItem = true;
            ApplyMetadata(node, item);
            trashRoot.Children.Add(node.Name, node);
        }

        trashRoot.ChildrenLoaded = true;
    }

    private void LoadListingError(CloudNode directory, Exception exception)
    {
        directory.Children.Clear();
        var node = NewNode("local:o2-listing-error", "O2 Cloud - error de listado.txt", CloudItemKind.File, directory);
        node.Attributes = FileSystemAttributes.Archive | FileSystemAttributes.Readonly;
        node.Content = Encoding.UTF8.GetBytes(
            "O2 Cloud Drive ha montado la unidad, pero O2 Cloud no ha devuelto el listado de esta carpeta.\r\n\r\n" +
            "La capacidad puede verse correctamente porque usa otro endpoint de O2 Cloud. " +
            "Desmonta la unidad, elimina la sesion guardada, haz Login nuevo y vuelve a montar.\r\n\r\n" +
            "Detalle tecnico:\r\n" +
            exception.Message);
        node.DeclaredSize = (ulong)node.Content.LongLength;
        directory.Children.Add(node.Name, node);
        directory.ChildrenLoaded = true;
    }

    private bool TryFindByPath(string path, out CloudNode node)
    {
        var normalized = PathTools.Normalize(path);
        if (normalized == "\\")
        {
            node = Root;
            return true;
        }

        node = Root;
        foreach (var segment in PathTools.GetSegments(normalized))
        {
            EnsureChildrenLoaded(node);
            if (!node.Children.TryGetValue(segment, out var child))
            {
                node = null!;
                return false;
            }

            node = child;
        }

        return true;
    }

    private CloudNode GetRequiredParent(string path)
    {
        var parentPath = PathTools.GetParent(path);
        if (!TryFindByPath(parentPath, out var parent))
        {
            throw new DirectoryNotFoundException("The parent directory does not exist.");
        }

        EnsureDirectory(parent);
        return parent;
    }

    private CloudNode NewNode(string id, string name, CloudItemKind kind, CloudNode? parent)
    {
        return new CloudNode(id, name, kind, parent, _nextIndex++);
    }

    private static void ApplyMetadata(CloudNode node, O2CloudItemDto item)
    {
        node.Id = item.Id;
        node.DeclaredSize = item.IsFolder ? 0UL : (ulong)Math.Max(0, item.Size);
        node.DirectUrl = string.IsNullOrWhiteSpace(item.DirectUrl) ? null : item.DirectUrl;
        node.MediaKind = item.MediaKind;
        node.Fingerprint = item.Fingerprint;
        node.Node = item.Node;
        node.DownloadToken = item.DownloadToken;
        node.ExplicitFileSize = null;
        node.HighestWrittenOffset = 0;
        node.UploadPendingRemoteConfirmation = IsPendingUploadId(item.Id);
        node.Attributes = item.IsFolder
            ? FileSystemAttributes.Directory
            : FileSystemAttributes.Archive;

        if (item.ModifiedAt is { } modifiedAt)
        {
            var fileTime = (ulong)modifiedAt.ToFileTime();
            node.LastWriteTime = fileTime;
            node.ChangeTime = fileTime;
        }
    }

    private static O2CloudItemDto ToDto(CloudNode node)
    {
        return new O2CloudItemDto(
            node.Id,
            node.Name,
            node.Parent?.Id,
            node.IsDirectory,
            checked((long)node.Size),
            DateTimeOffset.FromFileTime(checked((long)node.LastWriteTime)),
            node.DirectUrl,
            node.MediaKind,
            node.Fingerprint,
            node.Node,
            node.DownloadToken);
    }

    private byte[] ReadRemoteBytesWithCache(O2CloudItemDto item, ulong offset, uint length, ulong fileSize)
    {
        if (length == 0 || fileSize == 0)
        {
            return [];
        }

        if (length <= DirectReadThresholdBytes)
        {
            return _apiClient.DownloadFile(item, offset, length);
        }

        var result = new byte[length];
        var copied = 0;
        var firstBlock = offset / (ulong)_readCacheBlockSizeBytes;
        var lastBlock = (offset + length - 1) / (ulong)_readCacheBlockSizeBytes;

        for (var blockIndex = firstBlock; blockIndex <= lastBlock; blockIndex++)
        {
            var blockOffset = blockIndex * (ulong)_readCacheBlockSizeBytes;
            if (blockOffset >= fileSize)
            {
                break;
            }

            var blockLength = (uint)Math.Min((ulong)_readCacheBlockSizeBytes, fileSize - blockOffset);
            var block = GetOrDownloadReadBlock(item, blockIndex, blockOffset, blockLength);
            var sourceOffset = checked((int)Math.Min(offset > blockOffset ? offset - blockOffset : 0, (ulong)int.MaxValue));
            if (sourceOffset >= block.Length)
            {
                continue;
            }

            var available = Math.Min(block.Length - sourceOffset, result.Length - copied);
            if (available <= 0)
            {
                break;
            }

            Buffer.BlockCopy(block, sourceOffset, result, copied, available);
            copied += available;
        }

        ScheduleReadAhead(item, fileSize, lastBlock + 1);
        if (copied == result.Length)
        {
            return result;
        }

        Array.Resize(ref result, copied);
        return result;
    }

    private byte[] GetOrDownloadReadBlock(O2CloudItemDto item, ulong blockIndex, ulong blockOffset, uint blockLength)
    {
        var cacheKey = ReadCacheKey(item);
        var path = ReadCacheBlockPath(cacheKey, blockIndex);
        if (TryReadCachedBlock(path, blockLength, out var cached))
        {
            return cached;
        }

        var blockLock = GetReadBlockLock($"{cacheKey}:{blockIndex}");
        blockLock.Wait();
        try
        {
            if (TryReadCachedBlock(path, blockLength, out cached))
            {
                return cached;
            }

            var downloaded = _apiClient.DownloadFile(item, blockOffset, blockLength);
            if (downloaded.Length == blockLength)
            {
                WriteCachedBlock(path, downloaded);
                PruneReadCacheIfNeeded();
            }

            return downloaded;
        }
        finally
        {
            blockLock.Release();
        }
    }

    private SemaphoreSlim GetReadBlockLock(string key)
    {
        lock (_readCacheGate)
        {
            if (!_readBlockLocks.TryGetValue(key, out var blockLock))
            {
                blockLock = new SemaphoreSlim(1, 1);
                _readBlockLocks[key] = blockLock;
            }

            return blockLock;
        }
    }

    private static bool TryReadCachedBlock(string path, uint expectedLength, out byte[] content)
    {
        content = [];
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.Length != expectedLength)
            {
                return false;
            }

            content = File.ReadAllBytes(path);
            File.SetLastAccessTimeUtc(path, DateTime.UtcNow);
            return content.Length == expectedLength;
        }
        catch
        {
            content = [];
            return false;
        }
    }

    private static void WriteCachedBlock(string path, byte[] content)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllBytes(temporaryPath, content);
            File.Move(temporaryPath, path, overwrite: true);
        }
        catch
        {
            // Read cache must never make a filesystem read fail.
        }
    }

    private void ScheduleReadAhead(O2CloudItemDto item, ulong fileSize, ulong startBlock)
    {
        if (_readAheadBlocks <= 0 || !IsStreamable(item))
        {
            return;
        }

        var startOffset = startBlock * (ulong)_readCacheBlockSizeBytes;
        if (startOffset >= fileSize)
        {
            return;
        }

        var cacheKey = ReadCacheKey(item);
        var readAheadKey = $"{cacheKey}:{startBlock}:{_readAheadBlocks}";
        lock (_readCacheGate)
        {
            if (!_readAheadInFlight.Add(readAheadKey))
            {
                return;
            }
        }

        _ = Task.Run(() =>
        {
            try
            {
                for (var index = 0; index < _readAheadBlocks; index++)
                {
                    var blockIndex = startBlock + (ulong)index;
                    var blockOffset = blockIndex * (ulong)_readCacheBlockSizeBytes;
                    if (blockOffset >= fileSize)
                    {
                        break;
                    }

                    var blockLength = (uint)Math.Min((ulong)_readCacheBlockSizeBytes, fileSize - blockOffset);
                    GetOrDownloadReadBlock(item, blockIndex, blockOffset, blockLength);
                }
            }
            catch
            {
            }
            finally
            {
                lock (_readCacheGate)
                {
                    _readAheadInFlight.Remove(readAheadKey);
                }
            }
        });
    }

    private void PruneReadCacheIfNeeded()
    {
        if (_readCacheMaxBytes <= 0)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        lock (_readCacheGate)
        {
            if (now < _nextReadCachePruneAt)
            {
                return;
            }

            _nextReadCachePruneAt = now.Add(ReadCachePruneInterval);
        }

        try
        {
            if (!Directory.Exists(_readCacheDirectory))
            {
                return;
            }

            var files = Directory.EnumerateFiles(_readCacheDirectory, "*.blk", SearchOption.AllDirectories)
                .Select(path => new FileInfo(path))
                .Where(file => file.Exists)
                .OrderBy(file => file.LastAccessTimeUtc)
                .ToList();
            var totalBytes = files.Sum(file => file.Length);
            if (totalBytes <= _readCacheMaxBytes)
            {
                return;
            }

            var targetBytes = (long)(_readCacheMaxBytes * 0.8);
            foreach (var file in files)
            {
                try
                {
                    file.Delete();
                    totalBytes -= file.Length;
                }
                catch
                {
                }

                if (totalBytes <= targetBytes)
                {
                    break;
                }
            }
        }
        catch
        {
        }
    }

    private string ReadCacheBlockPath(string cacheKey, ulong blockIndex)
    {
        return Path.Combine(_readCacheDirectory, cacheKey, blockIndex.ToString("x16") + ".blk");
    }

    private static string ReadCacheKey(O2CloudItemDto item)
    {
        var identity = string.Join(
            "|",
            item.Id,
            item.Fingerprint,
            item.Size.ToString(System.Globalization.CultureInfo.InvariantCulture),
            item.ModifiedAt?.ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture),
            item.DirectUrl);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
    }

    private static bool IsStreamable(O2CloudItemDto item)
    {
        if (!string.IsNullOrWhiteSpace(item.MediaKind) &&
            (item.MediaKind.Contains("video", StringComparison.OrdinalIgnoreCase) ||
             item.MediaKind.Contains("audio", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        var extension = Path.GetExtension(item.Name);
        return !string.IsNullOrWhiteSpace(extension) &&
               StreamableExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
    }

    private void HydrateFile(CloudNode file)
    {
        EnsureFile(file);
        if (file.IsNew || HasCompleteLocalContent(file))
        {
            return;
        }

        EnsureEditableLength(file.Size);
        if (file.Size == 0)
        {
            file.Content = [];
            File.WriteAllBytes(EnsureLocalContentPath(file), []);
            return;
        }

        var content = _apiClient.DownloadFile(ToDto(file), 0, checked((uint)file.Size));
        File.WriteAllBytes(EnsureLocalContentPath(file), content);
        file.Content = [];
        file.DeclaredSize = (ulong)content.LongLength;
    }

    private static byte[] ReadLocalBytes(CloudNode file, ulong offset, uint length)
    {
        if (HasLocalContent(file))
        {
            using var stream = new FileStream(file.LocalContentPath!, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (offset >= (ulong)stream.Length)
            {
                return [];
            }

            var available = checked((int)Math.Min(length, (ulong)stream.Length - offset));
            var result = new byte[available];
            stream.Position = checked((long)offset);
            var read = stream.Read(result, 0, available);
            if (read == result.Length)
            {
                return result;
            }

            Array.Resize(ref result, read);
            return result;
        }

        if (offset >= (ulong)file.Content.LongLength)
        {
            return [];
        }

        var memoryAvailable = (uint)Math.Min(length, (ulong)file.Content.LongLength - offset);
        var memoryResult = new byte[memoryAvailable];
        Buffer.BlockCopy(file.Content, checked((int)offset), memoryResult, 0, checked((int)memoryAvailable));
        return memoryResult;
    }

    private static bool HasCompleteLocalContent(CloudNode file)
    {
        return HasLocalContent(file)
            ? file.Size == LocalContentLength(file)
            : file.Size == (ulong)file.Content.LongLength;
    }

    private static void EnsureEditableLength(ulong length)
    {
        if (length > MaxEditableFileBytes || length > long.MaxValue)
        {
            throw new IOException("El archivo supera el tamano maximo admitido por la subida de O2 Cloud.");
        }
    }

    private static bool CanHydrateForReadFallback(ulong length)
    {
        return length > 0 && length <= MaxEditableFileBytes && length <= uint.MaxValue;
    }

    private static bool HasLocalContent(CloudNode file)
    {
        return !string.IsNullOrWhiteSpace(file.LocalContentPath) && File.Exists(file.LocalContentPath);
    }

    private static bool IsPendingUploadId(string id)
    {
        return id.StartsWith("pending:upload:", StringComparison.OrdinalIgnoreCase);
    }

    private static ulong LocalContentLength(CloudNode file)
    {
        return HasLocalContent(file)
            ? checked((ulong)new FileInfo(file.LocalContentPath!).Length)
            : (ulong)file.Content.LongLength;
    }

    private Stream OpenLocalContentForRead(CloudNode file)
    {
        if (HasLocalContent(file))
        {
            return new FileStream(file.LocalContentPath!, FileMode.Open, FileAccess.Read, FileShare.Read);
        }

        return new MemoryStream(file.Content, writable: false);
    }

    private static Stream OpenCommittedContentForRead(string? contentPath, byte[]? contentBytes)
    {
        if (!string.IsNullOrWhiteSpace(contentPath))
        {
            return new FileStream(contentPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        }

        return new MemoryStream(contentBytes ?? [], writable: false);
    }

    private string EnsureLocalContentPath(CloudNode file)
    {
        if (string.IsNullOrWhiteSpace(file.LocalContentPath))
        {
            file.LocalContentPath = Path.Combine(_localContentDirectory, SafeCacheFileName(file.Id));
        }

        Directory.CreateDirectory(Path.GetDirectoryName(file.LocalContentPath)!);
        return file.LocalContentPath;
    }

    private static void DeleteLocalContent(CloudNode node)
    {
        if (string.IsNullOrWhiteSpace(node.LocalContentPath))
        {
            return;
        }

        try
        {
            if (File.Exists(node.LocalContentPath))
            {
                File.Delete(node.LocalContentPath);
            }
        }
        catch
        {
            // The OS will clean temporary files eventually; never fail a filesystem operation for cache cleanup.
        }
        finally
        {
            node.LocalContentPath = null;
        }
    }

    private static string SafeCacheFileName(string id)
    {
        var safe = string.Join("_", id.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
        return string.IsNullOrWhiteSpace(safe) ? Guid.NewGuid().ToString("N") : safe + ".bin";
    }

    private static void PurgeStaleWriteCaches()
    {
        if (!Directory.Exists(BaseWriteCacheDirectory))
        {
            return;
        }

        foreach (var directory in Directory.EnumerateDirectories(BaseWriteCacheDirectory))
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch
            {
                // A previous process may still be shutting down. Stale cache is ignored, never reused.
            }
        }
    }

    private static uint NormalizeFileAttributes(uint attributes)
    {
        var cleaned = attributes & ~FileSystemAttributes.Directory;
        return cleaned == 0 ? FileSystemAttributes.Archive : cleaned;
    }

    private static string SanitizeName(string name)
    {
        var trimmed = string.IsNullOrWhiteSpace(name) ? "sin-nombre" : name.Trim();
        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            trimmed = trimmed.Replace(invalid, '_');
        }

        return trimmed.TrimEnd('.', ' ');
    }

    private static string UniqueName(string preferredName, string itemId, ISet<string> usedNames)
    {
        var name = string.IsNullOrWhiteSpace(preferredName) ? "sin-nombre" : preferredName;
        if (usedNames.Add(name))
        {
            return name;
        }

        var extension = Path.GetExtension(name);
        var stem = string.IsNullOrWhiteSpace(extension) ? name : name[..^extension.Length];
        var suffix = itemId.Length > 8 ? itemId[..8] : itemId;
        var candidate = $"{stem} ({suffix}){extension}";
        var counter = 2;
        while (!usedNames.Add(candidate))
        {
            candidate = $"{stem} ({suffix}-{counter++}){extension}";
        }

        return candidate;
    }

    private static bool IsSameOrDescendant(CloudNode candidate, CloudNode ancestor)
    {
        var current = candidate;
        while (current is not null)
        {
            if (ReferenceEquals(current, ancestor))
            {
                return true;
            }

            current = current.Parent;
        }

        return false;
    }

    private static void EnsureDirectory(CloudNode node)
    {
        if (!node.IsDirectory)
        {
            throw new IOException("The node is not a directory.");
        }
    }

    private static void EnsureFile(CloudNode node)
    {
        if (node.IsDirectory)
        {
            throw new IOException("The node is a directory.");
        }
    }
}
