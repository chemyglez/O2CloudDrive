namespace O2CloudDrive.Auth;

public sealed class O2SessionUnavailableException : Exception
{
    public O2SessionUnavailableException()
        : base("No se pudo validar la sesion con O2 Cloud en este momento. Comprueba la conexion e intentalo de nuevo; la sesion guardada se ha conservado.")
    {
    }
}
