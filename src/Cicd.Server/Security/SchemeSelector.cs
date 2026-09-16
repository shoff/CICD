using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;

namespace Cicd.Server.Security;

/// <summary>
/// Picks the authentication scheme for a request. Bearer tokens shaped like JWTs go to the IdP validator, any other
/// bearer or X-Api-Key goes to the static token handler, and everything else is a browser session cookie.
/// </summary>
public static class SchemeSelector
{
    public const string SchemeName = "Smart";

    public static string Select(HttpRequest request, bool providerConfigured)
    {
        var token = PresentedToken(request);
        if (token is null)
        {
            return CookieAuthenticationDefaults.AuthenticationScheme;
        }
        return providerConfigured && LooksLikeJwt(token) ? JwtBearerDefaults.AuthenticationScheme : TokenAuthenticationHandler.SchemeName;
    }

    /// <summary>The credential a non-browser client sent: bearer header, X-Api-Key, or the hub access_token query.</summary>
    public static string? PresentedToken(HttpRequest request)
    {
        var header = request.Headers.Authorization.ToString();
        if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return header["Bearer ".Length..].Trim();
        }
        if (request.Headers.TryGetValue("X-Api-Key", out var apiKey) && !string.IsNullOrEmpty(apiKey))
        {
            return apiKey.ToString();
        }
        if (request.Path.StartsWithSegments("/hubs") && request.Query.TryGetValue("access_token", out var queryToken) && !string.IsNullOrEmpty(queryToken))
        {
            return queryToken.ToString();
        }
        return null;
    }

    /// <summary>Three non-empty base64url segments separated by dots. Static tokens never contain dots.</summary>
    public static bool LooksLikeJwt(string token)
    {
        var parts = token.Split('.');
        return parts.Length == 3 && parts.All(p => p.Length > 0 && p.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'));
    }
}
