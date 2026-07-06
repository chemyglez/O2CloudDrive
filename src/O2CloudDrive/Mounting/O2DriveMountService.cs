using Fsp;
using O2CloudDrive.Api;
using O2CloudDrive.VirtualFileSystem;

namespace O2CloudDrive.Mounting;

public sealed record DriveMountOptions(string MountPoint, string VolumeLabel, bool UseSimulatedData);

public sealed class O2DriveMountService : IDisposable
{
    private static readonly TimeSpan KeepAliveInitialDelay = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromMinutes(3);
    private readonly object _gate = new();
    private readonly IO2CloudApiClient _apiClient;
    private readonly ReadCacheOptions _readCacheOptions;
    private FileSystemHost? _host;
    private System.Threading.Timer? _keepAliveTimer;
    private int _keepAliveRunning;

    public O2DriveMountService(IO2CloudApiClient apiClient)
        : this(apiClient, ReadCacheOptions.Default)
    {
    }

    public O2DriveMountService(IO2CloudApiClient apiClient, ReadCacheOptions readCacheOptions)
    {
        _apiClient = apiClient;
        _readCacheOptions = readCacheOptions;
    }

    public bool IsMounted
    {
        get
        {
            lock (_gate)
            {
                return _host is not null;
            }
        }
    }

    public string? MountPoint { get; private set; }
    public string? VolumeLabel { get; private set; }
    public int? LastRootItemCount { get; private set; }

    public void Mount(DriveMountOptions options)
    {
        lock (_gate)
        {
            if (_host is not null)
            {
                throw new InvalidOperationException("La unidad ya esta montada.");
            }

            var mountPoint = NormalizeMountPoint(options.MountPoint);
            var volumeLabel = string.IsNullOrWhiteSpace(options.VolumeLabel)
                ? "O2 Cloud"
                : options.VolumeLabel.Trim();

            ICloudFileStore store = options.UseSimulatedData
                ? new InMemoryCloudFileStore()
                : new O2CloudFileStore(_apiClient, _readCacheOptions);
            LastRootItemCount = TryGetRootItemCount(store, options.UseSimulatedData);

            var fileSystem = new O2VirtualFileSystem(store, volumeLabel);
            var host = new FileSystemHost(fileSystem)
            {
                FileSystemName = "O2Cloud",
                SectorSize = 512,
                SectorsPerAllocationUnit = 8,
                CaseSensitiveSearch = false,
                CasePreservedNames = true,
                UnicodeOnDisk = true,
                PersistentAcls = false,
                PostCleanupWhenModifiedOnly = true,
                PassQueryDirectoryPattern = true,
                FlushAndPurgeOnCleanup = true,
            };

            var status = host.Mount(mountPoint, null, false, 0);
            if (status != FileSystemBase.STATUS_SUCCESS)
            {
                host.Dispose();
                throw new InvalidOperationException($"No se pudo montar {mountPoint}. NTSTATUS={status}");
            }

            _host = host;
            MountPoint = mountPoint;
            VolumeLabel = volumeLabel;
            StartKeepAlive(options.UseSimulatedData);
        }
    }

    public void Unmount()
    {
        FileSystemHost? host;
        System.Threading.Timer? keepAliveTimer;
        lock (_gate)
        {
            keepAliveTimer = _keepAliveTimer;
            _keepAliveTimer = null;
            host = _host;
            _host = null;
            MountPoint = null;
            VolumeLabel = null;
            LastRootItemCount = null;
        }

        keepAliveTimer?.Dispose();
        if (host is null)
        {
            return;
        }

        try
        {
            host.Unmount();
        }
        finally
        {
            host.Dispose();
        }
    }

    private void StartKeepAlive(bool disabled)
    {
        if (disabled)
        {
            return;
        }

        _keepAliveTimer?.Dispose();
        _keepAliveTimer = new System.Threading.Timer(
            _ => RunKeepAlive(),
            null,
            KeepAliveInitialDelay,
            KeepAliveInterval);
    }

    private void RunKeepAlive()
    {
        lock (_gate)
        {
            if (_host is null || _keepAliveTimer is null)
            {
                return;
            }
        }

        if (Interlocked.Exchange(ref _keepAliveRunning, 1) == 1)
        {
            return;
        }

        try
        {
            _apiClient.KeepSessionAlive();
        }
        finally
        {
            Interlocked.Exchange(ref _keepAliveRunning, 0);
        }
    }

    public void Dispose()
    {
        Unmount();
    }

    private static string NormalizeMountPoint(string mountPoint)
    {
        var trimmed = mountPoint.Trim().TrimEnd('\\');
        return trimmed.EndsWith(":", StringComparison.Ordinal) ? trimmed : $"{trimmed}:";
    }

    private static int? TryGetRootItemCount(ICloudFileStore store, bool useSimulatedData)
    {
        if (useSimulatedData)
        {
            return null;
        }

        try
        {
            var children = store.GetChildren(store.Root);
            if (children.Count == 1 &&
                children[0].Id.Equals("local:o2-listing-error", StringComparison.OrdinalIgnoreCase))
            {
                return 0;
            }

            return children.Count;
        }
        catch
        {
            return null;
        }
    }
}
