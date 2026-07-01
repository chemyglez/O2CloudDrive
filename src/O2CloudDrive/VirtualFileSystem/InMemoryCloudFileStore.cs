using System.Text;

namespace O2CloudDrive.VirtualFileSystem;

public sealed class InMemoryCloudFileStore : ICloudFileStore
{
    private readonly object _gate = new();
    private ulong _nextIndex = 1;

    public InMemoryCloudFileStore()
    {
        Root = NewNode("root", string.Empty, CloudItemKind.Directory, null);
        Seed();
    }

    public CloudNode Root { get; }

    public CloudVolumeInfo GetVolumeInfo()
    {
        return new CloudVolumeInfo(
            TotalSize: 10UL * 1024UL * 1024UL * 1024UL,
            FreeSize: 9UL * 1024UL * 1024UL * 1024UL);
    }

    public bool TryGetByPath(string path, out CloudNode node)
    {
        lock (_gate)
        {
            return TryFindByPath(path, out node!);
        }
    }

    public bool TryGetChild(CloudNode directory, string name, out CloudNode child)
    {
        lock (_gate)
        {
            child = null!;
            return directory.IsDirectory && directory.Children.TryGetValue(name, out child!);
        }
    }

    public IReadOnlyList<CloudNode> GetChildren(CloudNode directory)
    {
        lock (_gate)
        {
            EnsureDirectory(directory);
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
            var name = PathTools.GetName(normalized);
            var node = NewNode(Guid.NewGuid().ToString("N"), name, kind, parent);
            node.Attributes = kind == CloudItemKind.Directory
                ? FileSystemAttributes.Directory | (attributes & FileSystemAttributes.Readonly)
                : NormalizeFileAttributes(attributes);
            parent.Children.Add(name, node);
            Touch(parent, updateWriteTime: true);
            return node;
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

            if (node.IsDirectory && node.Children.Count > 0)
            {
                throw new IOException("The directory is not empty.");
            }

            node.Parent.Children.Remove(node.Name);
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

            var normalized = PathTools.Normalize(newPath);
            var newParent = GetRequiredParent(normalized);
            if (node.IsDirectory && IsSameOrDescendant(newParent, node))
            {
                throw new InvalidOperationException("A directory cannot be moved into itself.");
            }

            var newName = PathTools.GetName(normalized);
            if (newParent.Children.TryGetValue(newName, out var existing))
            {
                if (!replaceIfExists)
                {
                    throw new IOException("The target already exists.");
                }

                if (existing.IsDirectory && existing.Children.Count > 0)
                {
                    throw new IOException("The target directory is not empty.");
                }

                newParent.Children.Remove(existing.Name);
            }

            node.Parent.Children.Remove(node.Name);
            node.Parent = newParent;
            node.Name = newName;
            newParent.Children[newName] = node;
            Touch(node, updateWriteTime: false);
            Touch(newParent, updateWriteTime: true);
        }
    }

    public byte[] ReadAllBytes(CloudNode file)
    {
        lock (_gate)
        {
            EnsureFile(file);
            Touch(file, updateWriteTime: false);
            return file.Content.ToArray();
        }
    }

    public byte[] ReadBytes(CloudNode file, ulong offset, uint length)
    {
        lock (_gate)
        {
            EnsureFile(file);
            if (offset >= (ulong)file.Content.LongLength)
            {
                return [];
            }

            var available = (uint)Math.Min(length, (ulong)file.Content.LongLength - offset);
            var result = new byte[available];
            Buffer.BlockCopy(file.Content, checked((int)offset), result, 0, checked((int)available));
            Touch(file, updateWriteTime: false);
            return result;
        }
    }

    public uint WriteBytes(CloudNode file, ulong offset, byte[] buffer, bool writeToEndOfFile, bool constrainedIo)
    {
        lock (_gate)
        {
            EnsureFile(file);
            var writeOffset = writeToEndOfFile ? (ulong)file.Content.LongLength : offset;
            if (constrainedIo && writeOffset >= (ulong)file.Content.LongLength)
            {
                return 0;
            }

            var writable = constrainedIo
                ? (uint)Math.Min((ulong)buffer.Length, (ulong)file.Content.LongLength - writeOffset)
                : (uint)buffer.Length;

            var requiredLength = checked((long)(writeOffset + writable));
            if (requiredLength > file.Content.LongLength)
            {
                var content = file.Content;
                Array.Resize(ref content, checked((int)requiredLength));
                file.Content = content;
            }

            Buffer.BlockCopy(buffer, 0, file.Content, checked((int)writeOffset), checked((int)writable));
            file.DeclaredSize = (ulong)file.Content.LongLength;
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
            file.Content = content.ToArray();
            file.DeclaredSize = (ulong)file.Content.LongLength;
            file.IsDirty = true;
            Touch(file, updateWriteTime: true);
        }
    }

    public void SetFileSize(CloudNode file, ulong size)
    {
        lock (_gate)
        {
            EnsureFile(file);
            var content = file.Content;
            Array.Resize(ref content, checked((int)size));
            file.Content = content;
            file.DeclaredSize = size;
            file.IsDirty = true;
            Touch(file, updateWriteTime: true);
        }
    }

    public void Commit(CloudNode node)
    {
        node.IsDirty = false;
        node.IsNew = false;
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

    private void Seed()
    {
        var documents = Create("\\Documentos", CloudItemKind.Directory, FileSystemAttributes.Directory);
        var media = Create("\\Multimedia", CloudItemKind.Directory, FileSystemAttributes.Directory);
        var api = Create("\\O2 API", CloudItemKind.Directory, FileSystemAttributes.Directory);

        AddFile(documents, "README.txt", """
            O2 Cloud Drive - prototipo WinFsp

            Esta unidad es simulada. Puedes abrir este archivo, crear carpetas,
            escribir archivos nuevos y renombrar elementos desde el Explorador.

            La siguiente fase sustituira este almacen por llamadas reales a O2 Cloud.
            """);

        AddFile(documents, "nota-local.txt", "Archivo de prueba editable.\r\n");
        AddFile(media, "foto-demo.txt", "Marcador de imagen. En la fase O2 vendra de sapi/download/thumbnail.\r\n");
        AddFile(media, "video-demo.url", "[InternetShortcut]\r\nURL=https://cloud.o2online.es/\r\n");
        AddFile(api, "endpoints.txt", """
            Base API: https://cloud.o2online.es/sapi/
            Listar carpetas: GET sapi/media/folder?action=list&parentid={id}&limit=200
            Contenido: POST sapi/media?action=get&folderid={id}&limit=200
            Auth: validationkey en query string
            """);
    }

    private CloudNode AddFile(CloudNode parent, string name, string content)
    {
        var node = NewNode(Guid.NewGuid().ToString("N"), name, CloudItemKind.File, parent);
        node.Content = Encoding.UTF8.GetBytes(content.Replace("\n", "\r\n"));
        parent.Children.Add(name, node);
        return node;
    }

    private CloudNode NewNode(string id, string name, CloudItemKind kind, CloudNode? parent)
    {
        return new CloudNode(id, name, kind, parent, _nextIndex++);
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

    private static uint NormalizeFileAttributes(uint attributes)
    {
        var cleaned = attributes & ~FileSystemAttributes.Directory;
        return cleaned == 0 ? FileSystemAttributes.Archive : cleaned;
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
