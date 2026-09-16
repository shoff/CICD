using System.Security.Claims;
using Cicd.Core.Users;
using Microsoft.AspNetCore.Authentication;

namespace Cicd.Server.Security;

/// <summary>
/// Turns an IdP identity (session cookie or bearer JWT) into a CICD user: loads or creates the local row by issuer and
/// subject, then adds the role claims authorization runs on. Runs once per request for a given principal; static-token
/// identities pass through.
/// </summary>
public sealed class LocalUserClaimsTransformation(UserService users, IHttpContextAccessor accessor) : IClaimsTransformation
{
    public const string UserIdClaim = "cicd:user_id";
    public const string DisabledClaim = "cicd:disabled";
    private const string CacheKey = "cicd:transformed_principal";

    public async Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        var http = accessor.HttpContext;
        if (http?.Items.TryGetValue(CacheKey, out var cached) == true
            && cached is (ClaimsPrincipal source, ClaimsPrincipal done)
            && ReferenceEquals(source, principal))
        {
            return done;
        }
        var result = await ResolveAsync(principal, http?.RequestAborted ?? CancellationToken.None);
        if (http is not null)
        {
            http.Items[CacheKey] = (principal, result);
        }
        return result;
    }

    private async Task<ClaimsPrincipal> ResolveAsync(ClaimsPrincipal principal, CancellationToken cancellationToken)
    {
        if (principal.Identity is not ClaimsIdentity { IsAuthenticated: true } identity
            || identity.AuthenticationType == TokenAuthenticationHandler.SchemeName
            || identity.HasClaim(c => c.Type == UserIdClaim))
        {
            return principal;
        }
        var subject = identity.FindFirst("sub") ?? identity.FindFirst(ClaimTypes.NameIdentifier);
        if (subject is null)
        {
            return principal;
        }
        var external = new ExternalIdentity(
            identity.FindFirst("iss")?.Value ?? subject.Issuer,
            subject.Value,
            identity.FindFirst("username")?.Value ?? identity.FindFirst("preferred_username")?.Value,
            identity.FindFirst("email")?.Value,
            identity.FindFirst("name")?.Value);
        var user = await users.EnsureUserAsync(external, cancellationToken);

        var enriched = new ClaimsIdentity(identity.Claims, identity.AuthenticationType, ClaimTypes.Name, ClaimTypes.Role);
        enriched.AddClaim(new Claim(UserIdClaim, user.Id.ToString()));
        enriched.AddClaim(new Claim(ClaimTypes.Name, user.DisplayName ?? user.Username ?? user.Email ?? user.Subject));
        if (user.Disabled)
        {
            enriched.AddClaim(new Claim(DisabledClaim, "true"));
        }
        else
        {
            enriched.AddClaim(new Claim(ClaimTypes.Role, Roles.Of(user.Role)));
        }
        return new ClaimsPrincipal(enriched);
    }
}
