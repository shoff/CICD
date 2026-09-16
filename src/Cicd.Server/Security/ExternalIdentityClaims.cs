using System.Security.Claims;
using Cicd.Core.Users;

namespace Cicd.Server.Security;

/// <summary>The one place that knows which IdP claims describe a user. Used by the OIDC ticket handler and the claims transformation.</summary>
public static class ExternalIdentityClaims
{
    /// <summary>Null when the identity has no subject claim.</summary>
    public static ExternalIdentity? From(ClaimsIdentity identity)
    {
        var subject = identity.FindFirst("sub") ?? identity.FindFirst(ClaimTypes.NameIdentifier);
        if (subject is null)
        {
            return null;
        }
        return new ExternalIdentity(
            identity.FindFirst("iss")?.Value ?? subject.Issuer,
            subject.Value,
            identity.FindFirst("username")?.Value ?? identity.FindFirst("preferred_username")?.Value,
            identity.FindFirst("email")?.Value,
            identity.FindFirst("name")?.Value);
    }
}
