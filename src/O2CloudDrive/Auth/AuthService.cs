namespace O2CloudDrive.Auth;

public interface IAuthService
{
    O2Session GetCurrentSession();
    bool HasStoredSession();
    bool HasValidatedSession { get; }
    O2Session? EnsureAuthenticated(bool allowInteractive, bool forceInteractive = false);
    void InvalidateSession();
    bool InvalidateSessionIfCurrent(O2Session expectedSession);
    void Logout();
}

public sealed class AuthService : IAuthService
{
    private const string ValidationKeyVariable = "O2CLOUD_VALIDATIONKEY";
    private readonly ISessionStore _sessionStore;
    private readonly IInteractiveLoginService _loginService;
    private readonly O2SessionValidator _validator;
    private readonly object _sessionGate = new();
    private O2Session? _currentSession;
    private long _sessionGeneration;

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
        lock (_sessionGate)
        {
            return _currentSession ?? new O2Session();
        }
    }

    public bool HasStoredSession()
    {
        lock (_sessionGate)
        {
            if (_currentSession is { IsAuthenticated: true })
            {
                return true;
            }
        }

        return TryEnvironmentSession() is { IsAuthenticated: true } ||
               _sessionStore.TryRead() is { IsAuthenticated: true };
    }

    public bool HasValidatedSession
    {
        get
        {
            lock (_sessionGate)
            {
                return _currentSession is { IsAuthenticated: true };
            }
        }
    }

    public O2Session? EnsureAuthenticated(bool allowInteractive, bool forceInteractive = false)
    {
        long generation;
        lock (_sessionGate)
        {
            if (!forceInteractive && _currentSession is { IsAuthenticated: true })
            {
                return _currentSession;
            }

            generation = _sessionGeneration;
        }

        if (!forceInteractive)
        {
            var environmentSession = TryEnvironmentSession();
            if (environmentSession is not null)
            {
                switch (_validator.Validate(environmentSession))
                {
                    case O2SessionValidationStatus.Valid:
                        return TryPromoteSession(environmentSession, generation, persist: false)
                            ? environmentSession
                            : CurrentAuthenticatedSession();
                    case O2SessionValidationStatus.Unavailable:
                        if (!IsCurrentGeneration(generation))
                        {
                            return CurrentAuthenticatedSession();
                        }

                        throw new O2SessionUnavailableException();
                    case O2SessionValidationStatus.Rejected:
                        if (!MutateIfCurrent(generation, ClearEnvironmentSession))
                        {
                            return CurrentAuthenticatedSession();
                        }

                        break;
                }
            }

            var storedSession = _sessionStore.TryRead();
            if (storedSession is not null)
            {
                switch (_validator.Validate(storedSession))
                {
                    case O2SessionValidationStatus.Valid:
                        return TryPromoteSession(storedSession, generation, persist: false)
                            ? storedSession
                            : CurrentAuthenticatedSession();
                    case O2SessionValidationStatus.Unavailable:
                        if (!IsCurrentGeneration(generation))
                        {
                            return CurrentAuthenticatedSession();
                        }

                        throw new O2SessionUnavailableException();
                    case O2SessionValidationStatus.Rejected:
                        if (!MutateIfCurrent(generation, _sessionStore.Delete))
                        {
                            return CurrentAuthenticatedSession();
                        }

                        break;
                }
            }
        }
        else
        {
            InvalidateSession();
            _loginService.ClearSessionCache();
            generation = CurrentGeneration();
        }

        if (!allowInteractive)
        {
            return null;
        }

        if (!IsCurrentGeneration(generation))
        {
            return CurrentAuthenticatedSession();
        }

        var capturedSession = _loginService.Login(_validator);
        if (capturedSession is not { IsAuthenticated: true })
        {
            return null;
        }

        return TryPromoteSession(capturedSession, generation, persist: true)
            ? capturedSession
            : CurrentAuthenticatedSession();
    }

    public void Logout()
    {
        InvalidateSession();
        _loginService.ClearSessionCache();
    }

    public void InvalidateSession()
    {
        lock (_sessionGate)
        {
            InvalidateSessionLocked();
        }
    }

    public bool InvalidateSessionIfCurrent(O2Session expectedSession)
    {
        lock (_sessionGate)
        {
            if (!ReferenceEquals(_currentSession, expectedSession))
            {
                return false;
            }

            InvalidateSessionLocked();
            return true;
        }
    }

    private void InvalidateSessionLocked()
    {
        _sessionGeneration++;
        _currentSession = null;
        _sessionStore.Delete();
        ClearEnvironmentSession();
    }

    private O2Session? CurrentAuthenticatedSession()
    {
        lock (_sessionGate)
        {
            return _currentSession is { IsAuthenticated: true } ? _currentSession : null;
        }
    }

    private long CurrentGeneration()
    {
        lock (_sessionGate)
        {
            return _sessionGeneration;
        }
    }

    private bool IsCurrentGeneration(long generation)
    {
        lock (_sessionGate)
        {
            return generation == _sessionGeneration;
        }
    }

    private bool MutateIfCurrent(long generation, Action mutation)
    {
        lock (_sessionGate)
        {
            if (generation != _sessionGeneration)
            {
                return false;
            }

            mutation();
            return true;
        }
    }

    private bool TryPromoteSession(O2Session session, long generation, bool persist)
    {
        lock (_sessionGate)
        {
            if (generation != _sessionGeneration)
            {
                return false;
            }

            _currentSession = session;
            if (persist)
            {
                _sessionStore.Save(session);
            }

            return true;
        }
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
