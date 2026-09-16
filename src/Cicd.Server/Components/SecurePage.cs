using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;

namespace Cicd.Server.Components;

/// <summary>Base for pages with role-gated actions. Buttons hide through AuthorizeView; the action re-checks the policy server-side.</summary>
public abstract class SecurePage : ComponentBase
{
    [CascadingParameter] protected Task<AuthenticationState> AuthState { get; set; } = default!;
    [Inject] protected IAuthorizationService Authorization { get; set; } = default!;

    protected async Task<bool> AllowedAsync(string policy)
    {
        var state = await AuthState;
        return (await Authorization.AuthorizeAsync(state.User, policy)).Succeeded;
    }
}
