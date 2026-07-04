using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using O2CloudDrive.Config;

namespace O2CloudDrive.Updates;

public sealed partial class UpdateService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _httpClient;
    private readonly AppConfig _config;

    public UpdateService(HttpClient httpClient, AppConfig config)
    {
        _httpClient = httpClient;
        _config = config;
    }

    public async Task<UpdateCheckResult> CheckForUpdatesAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_config.UpdateOwner) ||
            string.IsNullOrWhiteSpace(_config.UpdateRepository))
        {
            return UpdateCheckResult.Current(AppVersion.TagName);
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));

            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"https://api.github.com/repos/{_config.UpdateOwner}/{_config.UpdateRepository}/releases");
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("O2CloudDrive", AppVersion.PackageVersion));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

            using var response = await _httpClient.SendAsync(request, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return UpdateCheckResult.Failed(
                    AppVersion.TagName,
                    $"GitHub devolvio {(int)response.StatusCode} {response.ReasonPhrase}.");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            var releases = await JsonSerializer.DeserializeAsync<List<GitHubRelease>>(stream, JsonOptions, timeout.Token)
                .ConfigureAwait(false);

            var current = VersionParts.Parse(AppVersion.TagName);
            if (releases is null || current is null)
            {
                return UpdateCheckResult.Current(AppVersion.TagName);
            }

            var latest = releases
                .Where(release => !release.Draft)
                .Where(release => _config.IncludePrereleaseUpdates || !release.Prerelease)
                .Select(release => new { Release = release, Version = VersionParts.Parse(release.TagName) })
                .Where(item => item.Version is not null)
                .OrderByDescending(item => item.Version)
                .FirstOrDefault();

            if (latest?.Version is null || latest.Version.CompareTo(current) <= 0)
            {
                return UpdateCheckResult.Current(AppVersion.TagName);
            }

            var installerAsset = latest.Release.Assets?
                .FirstOrDefault(asset =>
                    asset.Name.EndsWith("-Setup.exe", StringComparison.OrdinalIgnoreCase) ||
                    asset.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));

            var update = new UpdateRelease(
                latest.Release.TagName,
                string.IsNullOrWhiteSpace(latest.Release.Name) ? latest.Release.TagName : latest.Release.Name,
                latest.Release.HtmlUrl,
                installerAsset?.BrowserDownloadUrl,
                latest.Release.Body,
                latest.Release.Prerelease,
                latest.Release.PublishedAt);
            return new UpdateCheckResult(true, AppVersion.TagName, update, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return UpdateCheckResult.Failed(AppVersion.TagName, ex.Message);
        }
    }

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")]
        public string TagName { get; init; } = string.Empty;

        public string Name { get; init; } = string.Empty;

        [JsonPropertyName("html_url")]
        public string HtmlUrl { get; init; } = string.Empty;

        public string? Body { get; init; }

        public bool Draft { get; init; }

        public bool Prerelease { get; init; }

        [JsonPropertyName("published_at")]
        public DateTimeOffset? PublishedAt { get; init; }

        public List<GitHubAsset>? Assets { get; init; }
    }

    private sealed class GitHubAsset
    {
        public string Name { get; init; } = string.Empty;

        [JsonPropertyName("browser_download_url")]
        public string BrowserDownloadUrl { get; init; } = string.Empty;
    }

    private sealed partial class VersionParts : IComparable<VersionParts>
    {
        private VersionParts(int major, int minor, int patch, int prereleaseRank, int prereleaseNumber)
        {
            Major = major;
            Minor = minor;
            Patch = patch;
            PrereleaseRank = prereleaseRank;
            PrereleaseNumber = prereleaseNumber;
        }

        private int Major { get; }
        private int Minor { get; }
        private int Patch { get; }
        private int PrereleaseRank { get; }
        private int PrereleaseNumber { get; }

        public static VersionParts? Parse(string value)
        {
            var normalized = value.Trim().TrimStart('v', 'V').Replace('_', '-').Replace(' ', '-');
            var match = VersionRegex().Match(normalized);
            if (!match.Success)
            {
                return null;
            }

            var major = ParseNumber(match.Groups["major"].Value);
            var minor = ParseNumber(match.Groups["minor"].Value);
            var patch = ParseNumber(match.Groups["patch"].Value);
            var prereleaseText = match.Groups["pre"].Value;
            var prereleaseNumber = ParseNumber(match.Groups["preNum"].Value);
            return new VersionParts(major, minor, patch, GetPrereleaseRank(prereleaseText), prereleaseNumber);
        }

        public int CompareTo(VersionParts? other)
        {
            if (other is null)
            {
                return 1;
            }

            var result = Major.CompareTo(other.Major);
            if (result != 0) return result;
            result = Minor.CompareTo(other.Minor);
            if (result != 0) return result;
            result = Patch.CompareTo(other.Patch);
            if (result != 0) return result;
            result = PrereleaseRank.CompareTo(other.PrereleaseRank);
            return result != 0 ? result : PrereleaseNumber.CompareTo(other.PrereleaseNumber);
        }

        private static int ParseNumber(string value) => int.TryParse(value, out var number) ? number : 0;

        private static int GetPrereleaseRank(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return 100;
            }

            return value.ToLowerInvariant() switch
            {
                "rc" => 80,
                "beta" => 50,
                "b" => 50,
                "alpha" => 20,
                "a" => 20,
                _ => 10,
            };
        }

        [GeneratedRegex(@"^(?<major>\d+)(?:\.(?<minor>\d+))?(?:\.(?<patch>\d+))?(?:[-.]?(?<pre>alpha|a|beta|b|rc)(?<preNum>\d*)?)?$", RegexOptions.IgnoreCase)]
        private static partial Regex VersionRegex();
    }
}
