using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Fsp;
using WinFspFileInfo = Fsp.Interop.FileInfo;
using WinFspVolumeInfo = Fsp.Interop.VolumeInfo;

namespace O2CloudDrive.VirtualFileSystem;

public sealed class O2VirtualFileSystem : FileSystemBase
{
    private static readonly bool TraceReads =
        string.Equals(Environment.GetEnvironmentVariable("O2CLOUD_TRACE_READS"), "1", StringComparison.Ordinal);
    private static readonly bool TraceOperations =
        string.Equals(Environment.GetEnvironmentVariable("O2CLOUD_TRACE_OPERATIONS"), "1", StringComparison.Ordinal);
    private readonly ICloudFileStore _store;
    private string _volumeLabel;

    public O2VirtualFileSystem(ICloudFileStore store, string volumeLabel)
    {
        _store = store;
        _volumeLabel = volumeLabel;
    }

    public override int ExceptionHandler(Exception exception)
    {
        LogException(exception);
        return exception switch
        {
            DirectoryNotFoundException => STATUS_OBJECT_PATH_NOT_FOUND,
            FileNotFoundException => STATUS_OBJECT_NAME_NOT_FOUND,
            InvalidOperationException { Message: var message } when message.Contains("O2 Cloud", StringComparison.OrdinalIgnoreCase) => STATUS_UNSUCCESSFUL,
            InvalidOperationException { Message: var message } when message.Contains("directory", StringComparison.OrdinalIgnoreCase) => STATUS_NOT_A_DIRECTORY,
            IOException { Message: var message } when message.Contains("not empty", StringComparison.OrdinalIgnoreCase) => STATUS_DIRECTORY_NOT_EMPTY,
            IOException { Message: var message } when message.Contains("already exists", StringComparison.OrdinalIgnoreCase) => STATUS_OBJECT_NAME_COLLISION,
            IOException { Message: var message } when message.Contains("is a directory", StringComparison.OrdinalIgnoreCase) => STATUS_FILE_IS_A_DIRECTORY,
            IOException { Message: var message } when message.Contains("not a directory", StringComparison.OrdinalIgnoreCase) => STATUS_NOT_A_DIRECTORY,
            IOException { Message: var message } when message.Contains("O2 Cloud", StringComparison.OrdinalIgnoreCase) => STATUS_UNSUCCESSFUL,
            HttpRequestException => STATUS_UNSUCCESSFUL,
            UnauthorizedAccessException => STATUS_ACCESS_DENIED,
            _ => STATUS_UNSUCCESSFUL,
        };
    }

    public override int GetVolumeInfo(out WinFspVolumeInfo volumeInfo)
    {
        var cloudVolume = _store.GetVolumeInfo();
        volumeInfo = default;
        volumeInfo.TotalSize = cloudVolume.TotalSize;
        volumeInfo.FreeSize = cloudVolume.FreeSize;
        volumeInfo.SetVolumeLabel(_volumeLabel);
        return STATUS_SUCCESS;
    }

    public override int SetVolumeLabel(string volumeLabel, out WinFspVolumeInfo volumeInfo)
    {
        _volumeLabel = volumeLabel;
        return GetVolumeInfo(out volumeInfo);
    }

    public override int GetSecurityByName(string fileName, out uint fileAttributes, ref byte[] securityDescriptor)
    {
        fileAttributes = 0;
        if (!_store.TryGetByPath(fileName, out var node))
        {
            return STATUS_OBJECT_NAME_NOT_FOUND;
        }

        fileAttributes = ToFileInfo(node).FileAttributes;
        securityDescriptor = null!;
        return STATUS_SUCCESS;
    }

    public override int Create(
        string fileName,
        uint createOptions,
        uint grantedAccess,
        uint fileAttributes,
        byte[] securityDescriptor,
        ulong allocationSize,
        out object fileNode,
        out object fileDesc,
        out WinFspFileInfo fileInfo,
        out string normalizedName)
    {
        var kind = (createOptions & FILE_DIRECTORY_FILE) != 0
            ? CloudItemKind.Directory
            : CloudItemKind.File;

        var node = _store.Create(fileName, kind, fileAttributes);

        fileNode = node;
        fileDesc = new OpenFileHandle(node);
        fileInfo = ToFileInfo(node);
        normalizedName = node.FullPath;
        return STATUS_SUCCESS;
    }

    public override int Open(
        string fileName,
        uint createOptions,
        uint grantedAccess,
        out object fileNode,
        out object fileDesc,
        out WinFspFileInfo fileInfo,
        out string normalizedName)
    {
        fileNode = null!;
        fileDesc = null!;
        fileInfo = default;
        normalizedName = string.Empty;

        if (!_store.TryGetByPath(fileName, out var node))
        {
            return STATUS_OBJECT_NAME_NOT_FOUND;
        }

        if ((createOptions & FILE_DIRECTORY_FILE) != 0 && !node.IsDirectory)
        {
            return STATUS_NOT_A_DIRECTORY;
        }

        if ((createOptions & FILE_NON_DIRECTORY_FILE) != 0 && node.IsDirectory)
        {
            return STATUS_FILE_IS_A_DIRECTORY;
        }

        _store.Touch(node, updateWriteTime: false);
        fileNode = node;
        fileDesc = new OpenFileHandle(node);
        fileInfo = ToFileInfo(node);
        normalizedName = node.FullPath;
        TraceOperation(
            $"open {node.FullPath} directory={node.IsDirectory} size={node.Size} createOptions=0x{createOptions:X} grantedAccess=0x{grantedAccess:X}");
        return STATUS_SUCCESS;
    }

    public override int Overwrite(
        object fileNode,
        object fileDesc,
        uint fileAttributes,
        bool replaceFileAttributes,
        ulong allocationSize,
        out WinFspFileInfo fileInfo)
    {
        fileInfo = default;
        var node = GetNode(fileNode);
        if (node.IsDirectory)
        {
            return STATUS_FILE_IS_A_DIRECTORY;
        }

        _store.ReplaceContent(node, []);
        if (allocationSize > 0)
        {
            _store.SetFileSize(node, allocationSize);
        }

        if (replaceFileAttributes)
        {
            node.Attributes = fileAttributes == 0 ? FileSystemAttributes.Archive : fileAttributes;
        }
        else if (fileAttributes != 0)
        {
            node.Attributes |= fileAttributes;
        }

        fileInfo = ToFileInfo(node);
        return STATUS_SUCCESS;
    }

    public override void Cleanup(object fileNode, object fileDesc, string fileName, uint flags)
    {
        if (fileNode is not CloudNode node)
        {
            return;
        }

        var handle = fileDesc as OpenFileHandle;
        if ((flags & CleanupDelete) != 0 || handle?.DeleteOnClose == true)
        {
            _store.Delete(node);
            return;
        }

        _store.Commit(node);
    }

    public override void Close(object fileNode, object fileDesc)
    {
    }

    public override int Read(
        object fileNode,
        object fileDesc,
        IntPtr buffer,
        ulong offset,
        uint length,
        out uint bytesTransferred)
    {
        bytesTransferred = 0;
        var node = GetNode(fileNode);
        if (node.IsDirectory)
        {
            return STATUS_FILE_IS_A_DIRECTORY;
        }

        byte[] bytes;
        try
        {
            bytes = _store.ReadBytes(node, offset, length);
        }
        catch (Exception ex)
        {
            if (TraceReads)
            {
                Console.Error.WriteLine($"read-error {node.FullPath} offset={offset} length={length} {ex.GetType().Name}: {ex.Message}");
            }

            throw;
        }

        if (TraceReads)
        {
            Console.Error.WriteLine($"read {node.FullPath} offset={offset} length={length} bytes={bytes.Length}");
        }

        if (bytes.Length == 0)
        {
            return STATUS_SUCCESS;
        }

        Marshal.Copy(bytes, 0, buffer, bytes.Length);
        bytesTransferred = (uint)bytes.Length;
        return STATUS_SUCCESS;
    }

    public override int Write(
        object fileNode,
        object fileDesc,
        IntPtr buffer,
        ulong offset,
        uint length,
        bool writeToEndOfFile,
        bool constrainedIo,
        out uint bytesTransferred,
        out WinFspFileInfo fileInfo)
    {
        bytesTransferred = 0;
        fileInfo = default;
        var node = GetNode(fileNode);
        if (node.IsDirectory)
        {
            return STATUS_FILE_IS_A_DIRECTORY;
        }

        var bytes = new byte[checked((int)length)];
        Marshal.Copy(buffer, bytes, 0, checked((int)length));
        bytesTransferred = _store.WriteBytes(node, offset, bytes, writeToEndOfFile, constrainedIo);
        fileInfo = ToFileInfo(node);
        return STATUS_SUCCESS;
    }

    public override int Flush(object fileNode, object fileDesc, out WinFspFileInfo fileInfo)
    {
        fileInfo = default;
        if (fileNode is CloudNode node)
        {
            fileInfo = ToFileInfo(node);
        }

        return STATUS_SUCCESS;
    }

    public override int GetFileInfo(object fileNode, object fileDesc, out WinFspFileInfo fileInfo)
    {
        var node = GetNode(fileNode);
        fileInfo = ToFileInfo(node);
        TraceOperation($"info {node.FullPath} directory={node.IsDirectory} size={node.Size}");
        return STATUS_SUCCESS;
    }

    public override int SetBasicInfo(
        object fileNode,
        object fileDesc,
        uint fileAttributes,
        ulong creationTime,
        ulong lastAccessTime,
        ulong lastWriteTime,
        ulong changeTime,
        out WinFspFileInfo fileInfo)
    {
        var node = GetNode(fileNode);
        if (fileAttributes != uint.MaxValue)
        {
            node.Attributes = node.IsDirectory
                ? FileSystemAttributes.Directory | (fileAttributes & FileSystemAttributes.Readonly)
                : fileAttributes;
        }

        if (creationTime != 0) node.CreationTime = creationTime;
        if (lastAccessTime != 0) node.LastAccessTime = lastAccessTime;
        if (lastWriteTime != 0) node.LastWriteTime = lastWriteTime;
        if (changeTime != 0) node.ChangeTime = changeTime;

        fileInfo = ToFileInfo(node);
        return STATUS_SUCCESS;
    }

    public override int SetFileSize(
        object fileNode,
        object fileDesc,
        ulong newSize,
        bool setAllocationSize,
        out WinFspFileInfo fileInfo)
    {
        fileInfo = default;
        var node = GetNode(fileNode);
        if (node.IsDirectory)
        {
            return STATUS_FILE_IS_A_DIRECTORY;
        }

        if (!setAllocationSize)
        {
            _store.SetFileSize(node, newSize);
        }

        fileInfo = ToFileInfo(node);
        return STATUS_SUCCESS;
    }

    public override int CanDelete(object fileNode, object fileDesc, string fileName)
    {
        var node = GetNode(fileNode);
        if (node.Parent is null || node.IsVirtualTrashRoot)
        {
            return STATUS_ACCESS_DENIED;
        }

        return node.IsDirectory && !node.IsTrashItem && _store.GetChildren(node).Count > 0
            ? STATUS_DIRECTORY_NOT_EMPTY
            : STATUS_SUCCESS;
    }

    public override int SetDelete(object fileNode, object fileDesc, string fileName, bool deleteFile)
    {
        var node = GetNode(fileNode);
        if (deleteFile)
        {
            var status = CanDelete(fileNode, fileDesc, fileName);
            if (status != STATUS_SUCCESS)
            {
                return status;
            }
        }

        if (fileDesc is OpenFileHandle handle)
        {
            handle.DeleteOnClose = deleteFile;
        }

        return STATUS_SUCCESS;
    }

    public override int Rename(
        object fileNode,
        object fileDesc,
        string fileName,
        string newFileName,
        bool replaceIfExists)
    {
        _store.Rename(GetNode(fileNode), newFileName, replaceIfExists);
        return STATUS_SUCCESS;
    }

    public override bool ReadDirectoryEntry(
        object fileNode,
        object fileDesc,
        string pattern,
        string marker,
        ref object context,
        out string fileName,
        out WinFspFileInfo fileInfo)
    {
        fileName = string.Empty;
        fileInfo = default;
        var node = GetNode(fileNode);
        if (!node.IsDirectory)
        {
            return false;
        }

        if (context is not DirectoryEnumerationContext directoryContext)
        {
            directoryContext = CreateEnumerationContext(node, pattern, marker);
            context = directoryContext;
        }

        if (directoryContext.Index >= directoryContext.Entries.Count)
        {
            return false;
        }

        var entry = directoryContext.Entries[directoryContext.Index++];
        fileName = entry.Name;
        fileInfo = ToFileInfo(entry.Node);
        return true;
    }

    public override int GetDirInfoByName(
        object fileNode,
        object fileDesc,
        string fileName,
        out string normalizedName,
        out WinFspFileInfo fileInfo)
    {
        normalizedName = string.Empty;
        fileInfo = default;
        var directory = GetNode(fileNode);
        if (!directory.IsDirectory)
        {
            return STATUS_NOT_A_DIRECTORY;
        }

        if (!_store.TryGetChild(directory, fileName, out var child))
        {
            return STATUS_OBJECT_NAME_NOT_FOUND;
        }

        normalizedName = child.Name;
        fileInfo = ToFileInfo(child);
        return STATUS_SUCCESS;
    }

    private DirectoryEnumerationContext CreateEnumerationContext(CloudNode directory, string pattern, string marker)
    {
        var entries = _store.GetChildren(directory)
            .Where(child => MatchesPattern(child.Name, pattern))
            .OrderBy(child => child.Name, StringComparer.OrdinalIgnoreCase)
            .Select(child => new DirectoryEntry(child.Name, child))
            .ToList();

        if (!string.IsNullOrWhiteSpace(marker))
        {
            entries = entries
                .SkipWhile(entry => string.Compare(entry.Name, marker, StringComparison.OrdinalIgnoreCase) <= 0)
                .ToList();
        }

        return new DirectoryEnumerationContext(entries);
    }

    private static bool MatchesPattern(string name, string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern) || pattern == "*")
        {
            return true;
        }

        var regex = "^" + Regex.Escape(pattern)
            .Replace("\\*", ".*", StringComparison.Ordinal)
            .Replace("\\?", ".", StringComparison.Ordinal) + "$";

        return Regex.IsMatch(name, regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static CloudNode GetNode(object fileNode)
    {
        return fileNode as CloudNode ?? throw new InvalidOperationException("Invalid file node.");
    }

    private static void TraceOperation(string message)
    {
        if (TraceOperations)
        {
            Console.Error.WriteLine(message);
        }
    }

    private static void LogException(Exception exception)
    {
        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "O2CloudDrive",
                "Logs");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "filesystem-errors.log");
            File.AppendAllText(
                path,
                $"{DateTimeOffset.Now:u} {exception.GetType().Name}: {exception.Message}{Environment.NewLine}{exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
        }
    }

    private static WinFspFileInfo ToFileInfo(CloudNode node)
    {
        var size = node.Size;
        var allocationSize = node.IsDirectory ? 0UL : AlignAllocation(size);

        return new WinFspFileInfo
        {
            FileAttributes = node.IsDirectory
                ? FileSystemAttributes.Directory | (node.Attributes & FileSystemAttributes.Readonly)
                : node.Attributes,
            AllocationSize = allocationSize,
            FileSize = size,
            CreationTime = node.CreationTime,
            LastAccessTime = node.LastAccessTime,
            LastWriteTime = node.LastWriteTime,
            ChangeTime = node.ChangeTime,
            IndexNumber = node.IndexNumber,
            HardLinks = 1,
        };
    }

    private static ulong AlignAllocation(ulong size)
    {
        const ulong allocationUnit = 4096;
        return size == 0 ? 0 : ((size + allocationUnit - 1) / allocationUnit) * allocationUnit;
    }

    private sealed class OpenFileHandle(CloudNode node)
    {
        public CloudNode Node { get; } = node;
        public bool DeleteOnClose { get; set; }
    }
}
