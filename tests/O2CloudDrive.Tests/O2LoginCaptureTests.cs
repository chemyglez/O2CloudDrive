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
    public void SelectValidationKeys_UsesOnlyCurrentPageValueWhenPresent()
    {
        var keys = O2LoginForm.SelectValidationKeys(
            "fresh-key-123",
            ["captured-key-123"],
            ["remembered-key-123"]);

        Assert.Equal("fresh-key-123", keys[0]);
        Assert.Single(keys);
    }

    [Fact]
    public void SelectValidationKeys_DoesNotFallBackToRememberedWhenCaptureHasValue()
    {
        var keys = O2LoginForm.SelectValidationKeys(
            null,
            ["fresh-key-123", "second-key-123"],
            ["remembered-key-123"]);

        Assert.Equal(["fresh-key-123"], keys);
    }

    [Fact]
    public void SelectValidationKeys_UsesRememberedValueOnlyWithoutCurrentCapture()
    {
        var keys = O2LoginForm.SelectValidationKeys(
            null,
            [],
            ["remembered-key-123", "older-key-123"]);

        Assert.Equal(["remembered-key-123"], keys);
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
