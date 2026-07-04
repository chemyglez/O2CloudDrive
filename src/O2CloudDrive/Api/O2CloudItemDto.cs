namespace O2CloudDrive.Api;

public sealed record O2CloudItemDto(
    string Id,
    string Name,
    string? ParentId,
    bool IsFolder,
    long Size,
    DateTimeOffset? ModifiedAt,
    string? DirectUrl,
    string? MediaKind,
    string? Fingerprint,
    string? Node,
    string? DownloadToken);

public sealed record O2StorageInfo(long UsedBytes, long TotalBytes, long FreeBytes);

public sealed record O2ChangeSummary(bool HasChanges, long NextCursor);
