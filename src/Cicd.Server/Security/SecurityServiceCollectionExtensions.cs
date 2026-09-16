using System.Security.Claims;
using Cicd.Contracts;
using Cicd.Core.Users;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace Cicd.Server.Security;

public static class SecurityServiceCollectionExtensions
{
    public static IServiceCollection AddCicdSecurity(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<AgentsOptions>(configuration.GetSection(AgentsOptions.SectionName));
        services.Configure<OidcOptions>(configuration.GetSection(OidcOptions.SectionName));
        var oidc = configuration.GetSection(OidcOptions.SectionName).Get<OidcOptions>() ?? new OidcOptions();

        services.AddHttpContextAccessor();
        services.AddScoped<IClaimsTransformation, LocalUserClaimsTransformation>();

        var authentication = services.AddAuthentication(SchemeSelector.SchemeName)
            .AddPolicyScheme(SchemeSelector.SchemeName, "Cookie, IdP bearer or static token", options =>
                options.ForwardDefaultSelector = context => SchemeSelector.Select(context.Request, oidc.IsConfigured))
            .AddScheme<AuthenticationSchemeOptions, TokenAuthenticationHandler>(TokenAuthenticationHandler.SchemeName, null)
            .AddCookie(CookieAuthenticationDefaults.AuthenticationScheme, ConfigureCookie);

        if (oidc.IsConfigured)
        {
            authentication
                .AddJwtBearer(JwtBearerDefaults.AuthenticationScheme, options => ConfigureJwtBearer(options, oidc))
                .AddOpenIdConnect(OpenIdConnectDefaults.AuthenticationScheme, options => ConfigureOpenIdConnect(options, oidc));
        }

        services.AddSingleton<IAuthorizationHandler, RoleRequirementHandler>();
        services.AddAuthorizationBuilder()
            .AddPolicy(Policies.Agent, policy => policy.RequireRole(Roles.Agent))
            .AddPolicy(Policies.Viewer, policy => policy.AddRequirements(new RoleRequirement(UserRole.Viewer)))
            .AddPolicy(Policies.Developer, policy => policy.AddRequirements(new RoleRequirement(UserRole.Developer)))
            .AddPolicy(Policies.Admin, policy => policy.AddRequirements(new RoleRequirement(UserRole.Admin)));
        return services;
    }

    private static void ConfigureCookie(CookieAuthenticationOptions options)
    {
        options.Cookie.Name = "cicd.session";
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.LoginPath = "/login";
        options.LogoutPath = "/logout";
        options.AccessDeniedPath = "/access-denied";
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.SlidingExpiration = true;
        options.Events.OnRedirectToLogin = context => Respond(context, StatusCodes.Status401Unauthorized);
        options.Events.OnRedirectToAccessDenied = context => Respond(context, StatusCodes.Status403Forbidden);
    }

    /// <summary>API and hub callers get a status code; browsers get the redirect.</summary>
    private static Task Respond(RedirectContext<CookieAuthenticationOptions> context, int statusCode)
    {
        if (context.Request.Path.StartsWithSegments("/api") || context.Request.Path.StartsWithSegments("/hubs"))
        {
            context.Response.StatusCode = statusCode;
        }
        else
        {
            context.Response.Redirect(context.RedirectUri);
        }
        return Task.CompletedTask;
    }

    private static void ConfigureJwtBearer(JwtBearerOptions options, OidcOptions oidc)
    {
        options.Authority = oidc.Authority.TrimEnd('/');
        options.RequireHttpsMetadata = oidc.RequireHttpsMetadata;
        options.MapInboundClaims = false;
        options.TokenValidationParameters.NameClaimType = "name";
        options.TokenValidationParameters.RoleClaimType = ClaimTypes.Role;
        options.TokenValidationParameters.ValidateAudience = oidc.ValidateAudience;
        if (oidc.ValidateAudience)
        {
            options.Audience = oidc.Audience;
        }
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                if (context.Request.Path.StartsWithSegments("/hubs") && context.Request.Query.TryGetValue("access_token", out var token))
                {
                    context.Token = token.ToString();
                }
                return Task.CompletedTask;
            },
        };
    }

    private static void ConfigureOpenIdConnect(OpenIdConnectOptions options, OidcOptions oidc)
    {
        options.Authority = oidc.Authority.TrimEnd('/');
        options.ClientId = oidc.ClientId;
        options.ClientSecret = string.IsNullOrEmpty(oidc.ClientSecret) ? null : oidc.ClientSecret;
        options.RequireHttpsMetadata = oidc.RequireHttpsMetadata;
        options.SignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
        options.ResponseType = OpenIdConnectResponseType.Code;
        options.ResponseMode = OpenIdConnectResponseMode.Query;
        options.UsePkce = true;
        options.SaveTokens = false;
        options.GetClaimsFromUserInfoEndpoint = true;
        options.MapInboundClaims = false;
        options.TokenValidationParameters.NameClaimType = "name";
        options.TokenValidationParameters.RoleClaimType = ClaimTypes.Role;
        options.Scope.Clear();
        foreach (var scope in oidc.Scopes.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            options.Scope.Add(scope);
        }
        // Query response mode plus Lax cookies keeps the round trip working over plain http://localhost.
        options.CorrelationCookie.SameSite = SameSiteMode.Lax;
        options.NonceCookie.SameSite = SameSiteMode.Lax;
        if (!oidc.UsePushedAuthorization)
        {
            options.PushedAuthorizationBehavior = PushedAuthorizationBehavior.Disable;
        }
        options.Events.OnTicketReceived = OnTicketReceivedAsync;
    }

    /// <summary>Runs after id_token and userinfo claims are merged. Upserts the local user and issues a slim cookie principal.</summary>
    private static async Task OnTicketReceivedAsync(TicketReceivedContext context)
    {
        var principal = context.Principal ?? throw new InvalidOperationException("OIDC ticket has no principal.");
        var subject = principal.FindFirst("sub") ?? throw new InvalidOperationException("OIDC ticket has no 'sub' claim.");
        var identity = new ExternalIdentity(
            principal.FindFirst("iss")?.Value ?? subject.Issuer,
            subject.Value,
            principal.FindFirst("username")?.Value ?? principal.FindFirst("preferred_username")?.Value,
            principal.FindFirst("email")?.Value,
            principal.FindFirst("name")?.Value);
        var users = context.HttpContext.RequestServices.GetRequiredService<UserService>();
        await users.EnsureUserAsync(identity, context.HttpContext.RequestAborted);
        context.Principal = CookiePrincipal(identity);
    }

    /// <summary>Only identity claims go into the cookie. Role claims are added per request by <see cref="LocalUserClaimsTransformation"/>.</summary>
    public static ClaimsPrincipal CookiePrincipal(ExternalIdentity identity)
    {
        var claims = new List<Claim> { new("iss", identity.Issuer), new("sub", identity.Subject) };
        if (identity.Username is not null)
        {
            claims.Add(new Claim("username", identity.Username));
        }

        if (identity.Email is not null)
        {
            claims.Add(new Claim("email", identity.Email));
        }

        if (identity.DisplayName is not null)
        {
            claims.Add(new Claim("name", identity.DisplayName));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme, "name", ClaimTypes.Role));
    }
}
