namespace O2CloudDrive.Config;

public sealed record AppConfig
{
    public string MountPoint { get; init; } = "O:";
    public string VolumeLabel { get; init; } = "O2 Cloud";
    public string CacheDirectory { get; init; } = "%LOCALAPPDATA%\\O2CloudDrive\\Cache";
    public int ReadCacheBlockSizeBytes { get; init; } = 4 * 1024 * 1024;
    public int ReadAheadBlocks { get; init; } = 3;
    public long ReadCacheMaxBytes { get; init; } = 20L * 1024L * 1024L * 1024L;
    public string ApiBaseUrl { get; init; } = "https://cloud.o2online.es/sapi/";
    public string LoginUrl { get; init; } = "https://cloud.o2online.es/";
    public string CredentialTarget { get; init; } = "O2CloudDrive.Session";
    public string UpdateOwner { get; init; } = "chemyglez";
    public string UpdateRepository { get; init; } = "O2CloudDrive";
    public bool CheckForUpdatesOnStartup { get; init; } = true;
    public bool IncludePrereleaseUpdates { get; init; } = true;
    public bool UseSimulatedData { get; init; }
    public bool RequireAuthentication { get; init; } = true;
    public int? RunForSeconds { get; init; }
    public bool SelfTest { get; init; }
    public bool ApiProbe { get; init; }
    public bool Logout { get; init; }
    public string? SharePath { get; init; }
}
