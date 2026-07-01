namespace O2CloudDrive.VirtualFileSystem;

internal static class FileSystemAttributes
{
    public const uint Readonly = 0x00000001;
    public const uint Directory = 0x00000010;
    public const uint Archive = 0x00000020;
    public const uint Normal = 0x00000080;
}
