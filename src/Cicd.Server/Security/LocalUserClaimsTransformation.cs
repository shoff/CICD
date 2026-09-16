using System.Security.Claims;
using Cicd.Core.Users;
using Microsoft.AspNetCore.Authentication;

namespace Cicd.Server.Security;

/// <summary>
/// Turns an IdP identity (session cookie or bearer JWT) into a CICD user: loads or creates the local row by issuer and
/// subject, then adds the role claims authorization runs on. Any inbound <see cref="ClaimTypes.Role"/> or <c>cicd:</c>
/// claim is discarded first, so roles can only ever come from the local user row and never from the IdP. Runs once per
/// request for a given principal; static-token identities pass through.
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
            || identity.AuthenticationType == TokenAuthenticationHandler.SchemeName)
        {
            return principal;
        }
        var external = ExternalIdentityClaims.From(identity);
        if (external is null)
        {
            return principal;
        }
        var user = await users.EnsureUserAsync(external, cancellationToken);

        var kept = identity.Claims.Where(c => c.Type != ClaimTypes.Role && !c.Type.StartsWith("cicd:", StringComparison.Ordinal));
        var enriched = new ClaimsIdentity(kept, identity.AuthenticationType, ClaimTypes.Name, ClaimTypes.Role);
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
