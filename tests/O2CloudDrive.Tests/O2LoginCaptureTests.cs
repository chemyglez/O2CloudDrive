using O2CloudDrive.Auth;

namespace O2CloudDrive.Tests;

public sealed class O2LoginCaptureTests
{
    [Fact]
    public void CookieProbeUris_IncludeExactApiRootPath()
    {
        var uris = O2LoginForm.CookieProbeUris(
            new Uri("https://cloud.o2online.es/login"),
            new Uri("https://cloud.o2online.es/sapi/"));

        Assert.Contains(uris, uri => uri.AbsolutePath == "/sapi/media/folder/root");
    }

    [Fact]
    public void SelectValidationKeys_PrioritizesCurrentPageValueAndKeepsFreshAlternatives()
    {
        var keys = O2LoginForm.SelectValidationKeys(
            ["fresh-key-123"],
            ["captured-key-123", "fresh-key-123", "second-key-123"],
            ["remembered-key-123"]);

        Assert.Equal(["fresh-key-123", "remembered-key-123", "captured-key-123", "second-key-123"], keys);
    }

    [Fact]
    public void SelectValidationKeys_AddsRememberedCandidatesAfterFreshValues()
    {
        var keys = O2LoginForm.SelectValidationKeys(
            [],
            ["fresh-key-123", "second-key-123"],
            ["remembered-key-123"]);

        Assert.Equal(["remembered-key-123", "fresh-key-123", "second-key-123"], keys);
    }

    [Fact]
    public void SelectValidationKeys_UsesRememberedValueOnlyWithoutCurrentCapture()
    {
        var keys = O2LoginForm.SelectValidationKeys(
            [],
            [],
            ["remembered-key-123", "older-key-123"]);

        Assert.Equal(["remembered-key-123", "older-key-123"], keys);
    }

    [Fact]
    public void SelectValidationKeys_RetainsRememberedValidCandidateAfterStaleCapture()
    {
        var keys = O2LoginForm.SelectValidationKeys(
            [],
            ["stale-key-123"],
            ["current-key-123"]);

        Assert.Equal(["current-key-123", "stale-key-123"], keys);
    }

    [Fact]
    public void SelectValidationKeys_LimitsCandidatesAndRemovesDuplicates()
    {
        var keys = O2LoginForm.SelectValidationKeys(
            [],
            ["key-00000001", "key-00000002", "key-00000002", "key-00000003", "key-00000004", "key-00000005", "key-00000006"],
            ["remembered-key-123"]);

        Assert.Equal(["remembered-key-123", "key-00000001", "key-00000002", "key-00000003", "key-00000004"], keys);
    }

    [Fact]
    public void SelectValidationKeys_KeepsSuccessfulRootKeyFirstWhenLimitIsFull()
    {
        var keys = O2LoginForm.SelectValidationKeys(
            ["successful-root-key-123"],
            ["old-key-000001", "old-key-000002", "old-key-000003", "old-key-000004", "old-key-000005"],
            []);

        Assert.Equal(
            ["successful-root-key-123", "old-key-000001", "old-key-000002", "old-key-000003", "old-key-000004"],
            keys);
    }

    [Fact]
    public void ValidateCandidates_AcceptsSecondCandidateWhenFirstIsStale()
    {
        var stale = new O2Session { ValidationKey = "stale-key-123" };
        var current = new O2Session { ValidationKey = "current-key-123" };
        var attempts = new List<string>();

        var result = O2LoginForm.ValidateCandidates(
            [stale, current],
            session =>
            {
                attempts.Add(session.ValidationKey);
                return new O2SessionValidationResult(
                    ReferenceEquals(session, current)
                        ? O2SessionValidationStatus.Valid
                        : O2SessionValidationStatus.Rejected,
                    ReferenceEquals(session, current) ? current : null);
            });

        Assert.Same(current, result.Session);
        Assert.False(result.HadUnavailable);
        Assert.Equal(["stale-key-123", "current-key-123"], attempts);
    }

    [Fact]
    public void ValidateCandidates_ContinuesAfterUnavailableAndAcceptsLaterValid()
    {
        var unavailable = new O2Session { ValidationKey = "unavailable-key-123" };
        var current = new O2Session { ValidationKey = "current-key-123" };
        var attempts = new List<string>();

        var result = O2LoginForm.ValidateCandidates(
            [unavailable, current],
            session =>
            {
                attempts.Add(session.ValidationKey);
                return new O2SessionValidationResult(
                    ReferenceEquals(session, current)
                        ? O2SessionValidationStatus.Valid
                        : O2SessionValidationStatus.Unavailable,
                    ReferenceEquals(session, current) ? current : null);
            });

        Assert.Same(current, result.Session);
        Assert.True(result.HadUnavailable);
        Assert.Equal(["unavailable-key-123", "current-key-123"], attempts);
    }

    [Fact]
    public void ValidateCandidates_ReturnsRefreshedSessionFromValidator()
    {
        var original = new O2Session { ValidationKey = "original-key-123" };
        var refreshed = original with { ValidationKey = "refreshed-key-456" };

        var result = O2LoginForm.ValidateCandidates(
            [original],
            _ => new O2SessionValidationResult(O2SessionValidationStatus.Valid, refreshed));

        Assert.Same(refreshed, result.Session);
    }

    [Fact]
    public void SelectValidationKeys_DoesNotDropPreferredOrRecentKeysBehindStaleCaptures()
    {
        var keys = O2LoginForm.SelectValidationKeys(
            ["response-key-123", "cookie-key-123"],
            ["stale-key-0001", "stale-key-0002", "stale-key-0003", "stale-key-0004", "stale-key-0005", "stale-key-0006"],
            ["recent-key-123"]);

        Assert.Equal(
            ["response-key-123", "cookie-key-123", "recent-key-123", "stale-key-0001", "stale-key-0002"],
            keys);
    }

    [Theory]
    [InlineData("https://cloud.o2online.es/sapi/media/folder/root?action=get&validationkey=fresh-key-123")]
    [InlineData("https://cloud.o2online.es/sapi/media/folder/root?action=get&validation_key=fresh-key-123")]
    [InlineData("{\"validationkey\":\"fresh-key-123\"}")]
    [InlineData("{\"validationKey\":\"fresh-key-123\"}")]
    [InlineData("{\"validation_key\":\"fresh-key-123\"}")]
    public void ExtractValidationKeys_AcceptsKnownAliases(string source)
    {
        Assert.Equal(["fresh-key-123"], O2LoginForm.ExtractValidationKeys(source));
    }

    [Fact]
    public void ExtractFromCookies_AcceptsValidationKeyAndIgnoresOtherCookies()
    {
        var cookies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["JSESSIONID"] = "session-cookie-value",
            ["validationKey"] = "cookie-key-123",
        };

        Assert.Equal("cookie-key-123", O2ValidationKey.ExtractFromCookies(cookies));
    }

    [Theory]
    [InlineData("GET", 200, "https://cloud.o2online.es/sapi/media/folder/root?action=get&validationkey=fresh-key-123", "fresh-key-123")]
    [InlineData("GET", 200, "https://cloud.o2online.es/sapi/media/folder/root?action=get&validation_key=fresh-key-123", "fresh-key-123")]
    [InlineData("POST", 200, "https://cloud.o2online.es/sapi/media/folder/root?action=get&validationkey=fresh-key-123", null)]
    [InlineData("GET", 401, "https://cloud.o2online.es/sapi/media/folder/root?action=get&validationkey=fresh-key-123", null)]
    [InlineData("GET", 200, "https://cloud.o2online.es/sapi/media/folder/root?action=list&validationkey=fresh-key-123", null)]
    [InlineData("GET", 200, "https://other.example/sapi/media/folder/root?action=get&validationkey=fresh-key-123", null)]
    public void SuccessfulRootValidationKey_RequiresExactSuccessfulRootRequest(
        string method,
        int statusCode,
        string requestUri,
        string? expected)
    {
        Assert.Equal(
            expected,
            O2LoginForm.SuccessfulRootValidationKey(
                method,
                requestUri,
                statusCode,
                new Uri("https://cloud.o2online.es/sapi/")));
    }

    [Fact]
    public void ExtractValidationKeys_DoesNotCaptureFollowingPropertyWhenValueIsBlank()
    {
        const string state = "{\"validationkey\":\"\",\"sessionStorage\":{\"other\":\"value\"}}";

        Assert.Empty(O2LoginForm.ExtractValidationKeys(state));
    }

    [Fact]
    public void ExtractValidationKeys_ReturnsRecentQueryValue()
    {
        var keys = O2LoginForm.ExtractValidationKeys(
            "https://cloud.o2online.es/sapi/media/folder/root?action=get&validationkey=fresh-key-123");

        Assert.Equal(["fresh-key-123"], keys);
    }

    [Fact]
    public void ExtractValidationKeysFromJson_PrioritizesNamedKeyOverEmbeddedStaleUrl()
    {
        const string response = """
            {
              "url": "https://cloud.o2online.es/sapi/media/folder/root?action=get&validationkey=stale-key-123",
              "data": {
                "validationKey": "fresh-key-456",
                "folder": [{ "folderid": "root-test" }]
              }
            }
            """;

        Assert.Equal(["fresh-key-456"], O2LoginForm.ExtractValidationKeysFromJson(response));
    }

    [Fact]
    public void ExtractValidationKeysFromJson_AcceptsUnderscoreAliasWithoutScanningUrls()
    {
        const string response = """
            {
              "requestUrl": "https://cloud.o2online.es/sapi/media/folder/root?validation_key=stale-key-123",
              "validation_key": "fresh-key-456"
            }
            """;

        Assert.Equal(["fresh-key-456"], O2LoginForm.ExtractValidationKeysFromJson(response));
    }

    [Fact]
    public void ExtractValidationKeys_DecodesQueryValueExactlyOnce()
    {
        var keys = O2LoginForm.ExtractValidationKeys(
            "https://cloud.o2online.es/sapi/media/folder/root?action=get&validationkey=token%252Fvalue");

        Assert.Equal(["token%2Fvalue"], keys);
    }

    [Fact]
    public void ExtractValidationKeys_DoesNotDecodeJsonValue()
    {
        var keys = O2LoginForm.ExtractValidationKeys(
            "{\"validationKey\":\"token%2Fvalue\"}");

        Assert.Equal(["token%2Fvalue"], keys);
    }

    [Fact]
    public void ExtractFromCookies_DoesNotDecodeValue()
    {
        var cookies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["validationKey"] = "token%2Fvalue",
        };

        Assert.Equal("token%2Fvalue", O2ValidationKey.ExtractFromCookies(cookies));
    }

    [Fact]
    public void IsLatestRootResponse_RejectsOlderAsyncCompletion()
    {
        Assert.False(O2LoginForm.IsLatestRootResponse(responseOrdinal: 1, latestObservedOrdinal: 2));
        Assert.True(O2LoginForm.IsLatestRootResponse(responseOrdinal: 2, latestObservedOrdinal: 2));
    }

    [Theory]
    [InlineData("cloud.o2online.es", ".o2online.es", true)]
    [InlineData("cloud.o2online.es", "cloud.o2online.es", true)]
    [InlineData("maliciouso2online.es", ".o2online.es", false)]
    public void CookieDomainMatches_UsesDomainBoundary(string host, string cookieDomain, bool expected)
    {
        Assert.Equal(expected, O2LoginForm.CookieDomainMatches(host, cookieDomain));
    }
}
