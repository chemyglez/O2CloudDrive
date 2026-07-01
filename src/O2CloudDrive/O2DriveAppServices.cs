using O2CloudDrive.Api;
using O2CloudDrive.Auth;
using O2CloudDrive.Caching;
using O2CloudDrive.Config;
using O2CloudDrive.Mounting;

namespace O2CloudDrive;

public sealed class O2DriveAppServices : IDisposable
{
    private readonly HttpClient _httpClient;

    private O2DriveAppServices(
        AppConfig config,
        HttpClient httpClient,
        LocalCacheService cacheService,
        IAuthService authService,
        O2CloudApiClient apiClient,
        O2DriveMountService mountService)
    {
        Config = config;
        _httpClient = httpClient;
        CacheService = cacheService;
        AuthService = authService;
        ApiClient = apiClient;
        MountService = mountService;
    }

    public AppConfig Config { get; }
    public LocalCacheService CacheService { get; }
    public IAuthService AuthService { get; }
    public O2CloudApiClient ApiClient { get; }
    public O2DriveMountService MountService { get; }

    public static O2DriveAppServices Create(AppConfig config)
    {
        var cacheService = new LocalCacheService(config.CacheDirectory);
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(30),
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        };
        var httpClient = new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        var sessionValidator = new O2SessionValidator(httpClient, config.ApiBaseUrl);
        var sessionStore = new WindowsCredentialSessionStore(config.CredentialTarget);
        var loginService = new WebViewInteractiveLoginService(config.LoginUrl);
        var authService = new AuthService(sessionStore, loginService, sessionValidator);
        var apiClient = new O2CloudApiClient(httpClient, authService, config.ApiBaseUrl);
        var mountService = new O2DriveMountService(apiClient);
        return new O2DriveAppServices(config, httpClient, cacheService, authService, apiClient, mountService);
    }

    public void Dispose()
    {
        MountService.Dispose();
        _httpClient.Dispose();
    }
}
