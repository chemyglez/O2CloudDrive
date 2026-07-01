namespace O2CloudDrive.Auth;

public interface ISessionStore
{
    O2Session? TryRead();
    void Save(O2Session session);
    void Delete();
}
