using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Options;

namespace Cicd.Server.Security;

public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapCicdAuth(this IEndpointRouteBuilder app)
    {
        app.MapGet("/login", IResult (string? returnUrl, IOptions<OidcOptions> oidc) =>
        {
            if (!oidc.Value.IsConfigured)
            {
                return Results.NotFound();
            }
            var target = ReturnUrl.Sanitize(returnUrl);
            return Results.Challenge(new AuthenticationProperties { RedirectUri = target }, [OpenIdConnectDefaults.AuthenticationScheme]);
        }).ExcludeFromDescription();

        app.MapPost("/logout", async Task<IResult> (HttpContext http, IAntiforgery antiforgery, IOptions<OidcOptions> oidc) =>
        {
            if (!await antiforgery.IsRequestValidAsync(http))
            {
                return Results.BadRequest("Invalid antiforgery token.");
            }
            string[] schemes = oidc.Value.IsConfigured
                ? [CookieAuthenticationDefaults.AuthenticationScheme, OpenIdConnectDefaults.AuthenticationScheme]
                : [CookieAuthenticationDefaults.AuthenticationScheme];
            return Results.SignOut(new AuthenticationProperties { RedirectUri = "/" }, schemes);
        }).ExcludeFromDescription();
        return app;
    }
}
