namespace O2CloudDrive.VirtualFileSystem;

internal sealed class DirectoryEnumerationContext
{
    public DirectoryEnumerationContext(IReadOnlyList<DirectoryEntry> entries)
    {
        Entries = entries;
    }

    public IReadOnlyList<DirectoryEntry> Entries { get; }
    public int Index { get; set; }
}

internal sealed record DirectoryEntry(string Name, CloudNode Node);
