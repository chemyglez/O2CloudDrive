namespace O2CloudDrive.Auth;

public sealed class WebViewInteractiveLoginService : IInteractiveLoginService
{
    private readonly Uri _loginUri;
    private readonly Uri _apiBaseUri;

    public WebViewInteractiveLoginService(string loginUrl, string apiBaseUrl)
    {
        _loginUri = new Uri(loginUrl, UriKind.Absolute);
        _apiBaseUri = new Uri(apiBaseUrl, UriKind.Absolute);
    }

    public O2Session? Login(O2SessionValidator validator)
    {
        using var form = new O2LoginForm(_loginUri, _apiBaseUri, validator);
        return form.ShowDialog() == DialogResult.OK ? form.CapturedSession : null;
    }

    public void ClearSessionCache()
    {
        O2WebViewProfile.Clear();
    }
}
