using System.Security.Claims;
using Cicd.Contracts;
using Cicd.Core.Users;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;

namespace Cicd.Server.Security;

/// <summary>Requires a role of at least <see cref="Minimum"/>. The static API token carries the admin role and passes every level.</summary>
public sealed class RoleRequirement(UserRole minimum) : IAuthorizationRequirement
{
    public UserRole Minimum { get; } = minimum;
}

/// <summary>
/// Succeeds when the principal's highest role meets the minimum. Also succeeds for everyone in open mode (OIDC not
/// configured and no Security:ApiToken), which keeps the pre-login local development behavior.
/// </summary>
public sealed class RoleRequirementHandler(IOptions<OidcOptions> oidc, IOptions<SecurityOptions> security) : AuthorizationHandler<RoleRequirement>
{
    public bool OpenMode => !oidc.Value.IsConfigured && string.IsNullOrEmpty(security.Value.ApiToken);

    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, RoleRequirement requirement)
    {
        if (OpenMode || Level(context.User) >= requirement.Minimum)
        {
            context.Succeed(requirement);
        }
        return Task.CompletedTask;
    }

    /// <summary>The highest role the principal holds, or null for anonymous, agent-only or disabled principals.</summary>
    public static UserRole? Level(ClaimsPrincipal user)
    {
        if (user.IsInRole(Roles.Admin)) return UserRole.Admin;
        if (user.IsInRole(Roles.Developer)) return UserRole.Developer;
        if (user.IsInRole(Roles.Viewer)) return UserRole.Viewer;
        return null;
    }
}
