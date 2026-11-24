using Proctoring.Application.Errors;
using Proctoring.Infrastructure.Security;

namespace Proctoring.Api.Endpoints;

public static class BearerToken
{
    /// <summary>
    /// Extracts the token from "Authorization: Bearer &lt;token&gt;". Missing header, another scheme, several
    /// Authorization headers, or a value that is not in the token format -> 401. This is decided before any
    /// session lookup, so it never reveals whether a session exists. A well-formed token that does not belong to
    /// the session is answered with 404 by the service.
    /// </summary>
    public static string Read(HttpContext http)
    {
        var values = http.Request.Headers.Authorization;
        if (values.Count != 1)
            throw new InvalidCredentialsException(values.Count == 0
                ? "An 'Authorization: Bearer <sessionToken>' header is required"
                : "Exactly one Authorization header is allowed");
        var header = values[0] ?? "";
        const string scheme = "Bearer ";
        if (!header.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
            throw new InvalidCredentialsException("The Authorization scheme must be Bearer");
        var token = header[scheme.Length..].Trim();
        if (!SessionTokenService.IsWellFormed(token))
            throw new InvalidCredentialsException("The bearer token is malformed");
        return token;
    }
}
