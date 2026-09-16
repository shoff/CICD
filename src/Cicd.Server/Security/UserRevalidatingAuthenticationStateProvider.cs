using System.Security.Claims;
using Cicd.Core.Users;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;

namespace Cicd.Server.Security;

/// <summary>
/// Re-checks the local user row behind an interactive circuit so a disabled or demoted user loses access without
/// reloading the page. Static-token and anonymous principals carry no user id and are never revalidated.
/// </summary>
public class UserRevalidatingAuthenticationStateProvider(IServiceScopeFactory scopes, ILoggerFactory loggerFactory)
    : RevalidatingServerAuthenticationStateProvider(loggerFactory)
{
    protected override TimeSpan RevalidationInterval => TimeSpan.FromSeconds(30);

    protected override async Task<bool> ValidateAuthenticationStateAsync(AuthenticationState authenticationState, CancellationToken cancellationToken)
    {
        var user = authenticationState.User;
        if (!Guid.TryParse(user.FindFirst(LocalUserClaimsTransformation.UserIdClaim)?.Value, out var userId))
        {
            return true;
        }
        await using var scope = scopes.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserService>();
        var current = await users.FindAsync(userId, cancellationToken);
        return current is not null && !current.Disabled && user.IsInRole(Roles.Of(current.Role));
    }
}
