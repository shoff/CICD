using System.Security.Claims;
using Cicd.Core.Users;

namespace Cicd.Server.Security;

/// <summary>The one place that knows which IdP claims describe a user. Used by the v21 login client and the claims transformation.</summary>
public static class ExternalIdentityClaims
{
    /// <summary>Null when the identity has no subject claim. The subject claim's own issuer is the fallback issuer.</summary>
    public static ExternalIdentity? From(ClaimsIdentity identity)
    {
        var subject = identity.FindFirst("sub") ?? identity.FindFirst(ClaimTypes.NameIdentifier);
        if (subject is null)
        {
            return null;
        }
        return From(identity.Claims, subject.Issuer);
    }

    /// <summary>
    /// Maps a claim set onto an <see cref="ExternalIdentity"/>. Null when there is no subject claim. The probe order is
    /// the contract between the cookie, the bearer JWT and the v21 access token, so it lives here only.
    /// </summary>
    public static ExternalIdentity? From(IEnumerable<Claim> claims, string fallbackIssuer)
    {
        var all = claims as IReadOnlyCollection<Claim> ?? [.. claims];
        string? First(params string[] types) =>
            types.Select(t => all.FirstOrDefault(c => c.Type == t)?.Value).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
        var subject = First("sub", ClaimTypes.NameIdentifier);
        if (subject is null)
        {
            return null;
        }
        return new ExternalIdentity(
            (First("iss") ?? fallbackIssuer).TrimEnd('/'),
            subject,
            First("username", "unique_name", "preferred_username"),
            First("email"),
            First("name", "full_name"));
    }
}
