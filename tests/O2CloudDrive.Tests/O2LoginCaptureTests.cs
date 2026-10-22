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
            "fresh-key-123",
            ["captured-key-123", "fresh-key-123", "second-key-123"],
            ["remembered-key-123"]);

        Assert.Equal(["fresh-key-123", "captured-key-123", "second-key-123", "remembered-key-123"], keys);
    }

    [Fact]
    public void SelectValidationKeys_AddsRememberedCandidatesAfterFreshValues()
    {
        var keys = O2LoginForm.SelectValidationKeys(
            null,
            ["fresh-key-123", "second-key-123"],
            ["remembered-key-123"]);

        Assert.Equal(["fresh-key-123", "second-key-123", "remembered-key-123"], keys);
    }

    [Fact]
    public void SelectValidationKeys_UsesRememberedValueOnlyWithoutCurrentCapture()
    {
        var keys = O2LoginForm.SelectValidationKeys(
            null,
            [],
            ["remembered-key-123", "older-key-123"]);

        Assert.Equal(["remembered-key-123", "older-key-123"], keys);
    }

    [Fact]
    public void SelectValidationKeys_RetainsRememberedValidCandidateAfterStaleCapture()
    {
        var keys = O2LoginForm.SelectValidationKeys(
            null,
            ["stale-key-123"],
            ["current-key-123"]);

        Assert.Equal(["stale-key-123", "current-key-123"], keys);
    }

    [Fact]
    public void SelectValidationKeys_LimitsCandidatesAndRemovesDuplicates()
    {
        var keys = O2LoginForm.SelectValidationKeys(
            null,
            ["key-00000001", "key-00000002", "key-00000002", "key-00000003", "key-00000004", "key-00000005", "key-00000006"],
            ["remembered-key-123"]);

        Assert.Equal(["key-00000001", "key-00000002", "key-00000003", "key-00000004", "key-00000005"], keys);
    }

    [Fact]
    public void SelectValidationKeys_KeepsSuccessfulRootKeyFirstWhenLimitIsFull()
    {
        var keys = O2LoginForm.SelectValidationKeys(
            "successful-root-key-123",
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
                return ReferenceEquals(session, current)
                    ? O2SessionValidationStatus.Valid
                    : O2SessionValidationStatus.Rejected;
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
                return ReferenceEquals(session, current)
                    ? O2SessionValidationStatus.Valid
                    : O2SessionValidationStatus.Unavailable;
            });

        Assert.Same(current, result.Session);
        Assert.True(result.HadUnavailable);
        Assert.Equal(["unavailable-key-123", "current-key-123"], attempts);
    }

    [Theory]
    [InlineData("GET", 200, "https://cloud.o2online.es/sapi/media/folder/root?action=get&validationkey=fresh-key-123", "fresh-key-123")]
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

    [Theory]
    [InlineData("cloud.o2online.es", ".o2online.es", true)]
    [InlineData("cloud.o2online.es", "cloud.o2online.es", true)]
    [InlineData("maliciouso2online.es", ".o2online.es", false)]
    public void CookieDomainMatches_UsesDomainBoundary(string host, string cookieDomain, bool expected)
    {
        Assert.Equal(expected, O2LoginForm.CookieDomainMatches(host, cookieDomain));
    }
}
