using System.Net;
using O2CloudDrive.Config;
using O2CloudDrive.Updates;

namespace O2CloudDrive.Tests;

public sealed class UpdateServiceTests
{
    [Fact]
    public async Task CheckForUpdatesAsync_PrefersFullSetupRegardlessOfAssetOrder()
    {
        const string response = """
            [{
              "tag_name": "v9.0.0",
              "name": "O2 Cloud Drive 9.0.0",
              "html_url": "https://github.example/releases/v9.0.0",
              "draft": false,
              "prerelease": false,
              "assets": [
                { "name": "O2CloudDrive-9.0.0-Update.exe", "browser_download_url": "https://download.example/update" },
                { "name": "O2CloudDrive-9.0.0-Setup.exe", "browser_download_url": "https://download.example/setup" }
              ]
            }]
            """;
        using var httpClient = new HttpClient(new JsonHandler(response));
        var service = new UpdateService(httpClient, new AppConfig
        {
            UpdateOwner = "owner",
            UpdateRepository = "repository",
        });

        var result = await service.CheckForUpdatesAsync();

        Assert.True(result.IsUpdateAvailable);
        Assert.Equal("https://download.example/setup", result.Release?.InstallerDownloadUrl);
    }

    private sealed class JsonHandler(string response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response),
            });
        }
    }
}
