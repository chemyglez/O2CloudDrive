namespace O2CloudDrive.Updates;

public sealed record UpdateCheckResult(
    bool IsUpdateAvailable,
    string CurrentTag,
    UpdateRelease? Release,
    string? ErrorMessage)
{
    public static UpdateCheckResult Current(string currentTag) => new(false, currentTag, null, null);

    public static UpdateCheckResult Failed(string currentTag, string errorMessage) => new(false, currentTag, null, errorMessage);
}
