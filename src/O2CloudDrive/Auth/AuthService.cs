namespace O2CloudDrive.Auth;

public interface IAuthService
{
    O2Session GetCurrentSession();
    bool HasStoredSession();
    bool HasValidatedSession { get; }
    O2Session? EnsureAuthenticated(bool allowInteractive, bool forceInteractive = false);
    void Logout();
}

public sealed class AuthService : IAuthService
{
    private const string ValidationKeyVariable = "O2CLOUD_VALIDATIONKEY";
    private readonly ISessionStore _sessionStore;
    private readonly IInteractiveLoginService _loginService;
    private readonly O2SessionValidator _validator;
    private O2Session? _currentSession;

    public AuthService(
        ISessionStore sessionStore,
        IInteractiveLoginService loginService,
        O2SessionValidator validator)
    {
        _sessionStore = sessionStore;
        _loginService = loginService;
        _validator = validator;
    }

    public O2Session GetCurrentSession()
    {
        return _currentSession ?? TryEnvironmentSession() ?? _sessionStore.TryRead() ?? new O2Session();
    }

    public bool HasStoredSession()
    {
        return _currentSession is { IsAuthenticated: true } ||
               TryEnvironmentSession() is { IsAuthenticated: true } ||
               _sessionStore.TryRead() is { IsAuthenticated: true };
    }

    public bool HasValidatedSession => _currentSession is { IsAuthenticated: true };

    public O2Session? EnsureAuthenticated(bool allowInteractive, bool forceInteractive = false)
    {
        if (!forceInteractive)
        {
            var environmentSession = TryEnvironmentSession();
            if (Validate(environmentSession))
            {
                _currentSession = environmentSession;
                return _currentSession;
            }
            else if (environmentSession is not null)
            {
                ClearEnvironmentSession();
            }

            var storedSession = _sessionStore.TryRead();
            if (Validate(storedSession))
            {
                _currentSession = storedSession;
                return _currentSession;
            }

            if (storedSession is not null)
            {
                _sessionStore.Delete();
            }
        }
        else
        {
            _currentSession = null;
            _sessionStore.Delete();
            ClearEnvironmentSession();
        }

        if (!allowInteractive)
        {
            return null;
        }

        var capturedSession = _loginService.Login(_validator);
        if (capturedSession is not { IsAuthenticated: true })
        {
            return null;
        }

        _currentSession = capturedSession;
        _sessionStore.Save(capturedSession!);
        return _currentSession;
    }

    public void Logout()
    {
        _currentSession = null;
        _sessionStore.Delete();
        ClearEnvironmentSession();
        _loginService.ClearSessionCache();
    }

    private bool Validate(O2Session? session)
    {
        return session is { IsAuthenticated: true } && _validator.Validate(session);
    }

    private static O2Session? TryEnvironmentSession()
    {
        var validationKey = Environment.GetEnvironmentVariable(ValidationKeyVariable);
        if (string.IsNullOrWhiteSpace(validationKey))
        {
            return null;
        }

        return new O2Session
        {
            ValidationKey = validationKey.Trim(),
        };
    }

    private static void ClearEnvironmentSession()
    {
        Environment.SetEnvironmentVariable(ValidationKeyVariable, null, EnvironmentVariableTarget.Process);
        TryClearEnvironmentVariable(EnvironmentVariableTarget.User);
        TryClearEnvironmentVariable(EnvironmentVariableTarget.Machine);
    }

    private static void TryClearEnvironmentVariable(EnvironmentVariableTarget target)
    {
        try
        {
            Environment.SetEnvironmentVariable(ValidationKeyVariable, null, target);
        }
        catch
        {
            // Machine-level variables can require elevation; process and user cleanup still remove local reuse.
        }
    }
}
