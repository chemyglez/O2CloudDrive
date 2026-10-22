using System.Net;

namespace O2CloudDrive.Auth;

public sealed class O2AuthenticationException : Exception
{
    public O2AuthenticationException(HttpStatusCode statusCode, string resource)
        : base($"O2 Cloud rechazo la sesion al consultar {resource} (HTTP {(int)statusCode}).")
    {
        StatusCode = statusCode;
        Resource = resource;
    }

    public HttpStatusCode StatusCode { get; }
    public string Resource { get; }
}
