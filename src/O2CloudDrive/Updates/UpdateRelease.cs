namespace O2CloudDrive.Updates;

public sealed record UpdateRelease(
    string TagName,
    string Name,
    string HtmlUrl,
    string? InstallerDownloadUrl,
    string? Body,
    bool Prerelease,
    DateTimeOffset? PublishedAt);
