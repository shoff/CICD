using Cicd.Core.Users;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Cicd.Server.Security;

public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapCicdAuth(this IEndpointRouteBuilder app)
    {
        // GET /login is the Blazor page. This handles the form it renders. Antiforgery is validated by the middleware
        // for form-bound endpoints; the page emits the token with <AntiforgeryToken />.
        app.MapPost("/login", async Task<IResult> (
            [FromForm] string? userName, [FromForm] string? password, [FromForm] string? returnUrl,
            HttpContext http, V21LoginClient login, UserService users, IOptions<IdentityProviderOptions> provider) =>
        {
            if (!provider.Value.IsConfigured)
            {
                return Results.NotFound();
            }
            var target = ReturnUrl.Sanitize(returnUrl);
            if (string.IsNullOrWhiteSpace(userName) || string.IsNullOrWhiteSpace(password))
            {
                return Results.Redirect(LoginPage("required", target));
            }
            var result = await login.LoginAsync(userName.Trim(), password, http.RequestAborted);
            if (result.Outcome == LoginOutcome.ProviderUnavailable)
            {
                return Results.Redirect(LoginPage("unavailable", target));
            }
            if (result.Identity is null)
            {
                return Results.Redirect(LoginPage("invalid", target));
            }
            await users.EnsureUserAsync(result.Identity, http.RequestAborted);
            await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, SecurityServiceCollectionExtensions.CookiePrincipal(result.Identity));
            return Results.Redirect(target);
        }).ExcludeFromDescription();

        app.MapPost("/logout", async Task<IResult> (HttpContext http, IAntiforgery antiforgery) =>
        {
            if (!await antiforgery.IsRequestValidAsync(http))
            {
                return Results.BadRequest("Invalid antiforgery token.");
            }
            return Results.SignOut(new AuthenticationProperties { RedirectUri = "/" }, [CookieAuthenticationDefaults.AuthenticationScheme]);
        }).ExcludeFromDescription();
        return app;
    }

    private static string LoginPage(string error, string returnUrl) => $"/login?error={error}&returnUrl={Uri.EscapeDataString(returnUrl)}";
}
