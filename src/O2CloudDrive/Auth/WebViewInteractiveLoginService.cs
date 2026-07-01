namespace O2CloudDrive.Auth;

public sealed class WebViewInteractiveLoginService : IInteractiveLoginService
{
    private readonly Uri _loginUri;

    public WebViewInteractiveLoginService(string loginUrl)
    {
        _loginUri = new Uri(loginUrl, UriKind.Absolute);
    }

    public O2Session? Login(O2SessionValidator validator)
    {
        using var form = new O2LoginForm(_loginUri, validator);
        return form.ShowDialog() == DialogResult.OK ? form.CapturedSession : null;
    }

    public void ClearSessionCache()
    {
        O2WebViewProfile.Clear();
    }
}
