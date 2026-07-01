namespace O2CloudDrive.Auth;

public interface IInteractiveLoginService
{
    O2Session? Login(O2SessionValidator validator);
    void ClearSessionCache();
}
