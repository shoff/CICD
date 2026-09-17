using System.Security.Claims;
using Cicd.Contracts;
using Cicd.Core.Users;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;

namespace Cicd.Server.Security;

public static class SecurityServiceCollectionExtensions
{
    public static IServiceCollection AddCicdSecurity(this IServiceCollection services, IConfiguration configuration, bool isDevelopment)
    {
        services.Configure<AgentsOptions>(configuration.GetSection(AgentsOptions.SectionName));
        services.Configure<IdentityProviderOptions>(configuration.GetSection(IdentityProviderOptions.SectionName));
        var provider = configuration.GetSection(IdentityProviderOptions.SectionName).Get<IdentityProviderOptions>() ?? new IdentityProviderOptions();

        // A plain-http login URL is not a boot failure: the settings are in the database now, and refusing to start
        // would lock the operator out of the page that fixes it. SettingsService rejects the combination on write, and
        // V21LoginClient refuses the call at sign-in time.
        services.AddHttpClient<V21LoginClient>();
        services.AddHttpContextAccessor();
        services.AddScoped<IClaimsTransformation, LocalUserClaimsTransformation>();
        services.AddScoped<AuthenticationStateProvider, UserRevalidatingAuthenticationStateProvider>();

        var authentication = services.AddAuthentication(SchemeSelector.SchemeName)
            .AddPolicyScheme(SchemeSelector.SchemeName, "Cookie, IdP bearer or static token", options =>
                options.ForwardDefaultSelector = context => SchemeSelector.Select(context.Request, providerConfigured: provider.IsConfigured))
            .AddScheme<AuthenticationSchemeOptions, TokenAuthenticationHandler>(TokenAuthenticationHandler.SchemeName, null)
            .AddCookie(CookieAuthenticationDefaults.AuthenticationScheme, options => ConfigureCookie(options, isDevelopment));

        if (provider.IsConfigured)
        {
            authentication.AddJwtBearer(JwtBearerDefaults.AuthenticationScheme, options => ConfigureJwtBearer(options, provider));
        }

        services.AddSingleton<IAuthorizationHandler, RoleRequirementHandler>();
        services.AddAuthorizationBuilder()
            .AddPolicy(Policies.Agent, policy => policy.RequireRole(Roles.Agent))
            .AddPolicy(Policies.Viewer, policy => policy.AddRequirements(new RoleRequirement(UserRole.Viewer)))
            .AddPolicy(Policies.Developer, policy => policy.AddRequirements(new RoleRequirement(UserRole.Developer)))
            .AddPolicy(Policies.Admin, policy => policy.AddRequirements(new RoleRequirement(UserRole.Admin)));
        return services;
    }

    private static void ConfigureCookie(CookieAuthenticationOptions options, bool isDevelopment)
    {
        options.Cookie.Name = "cicd.session";
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = isDevelopment ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
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

    private static void ConfigureJwtBearer(JwtBearerOptions options, IdentityProviderOptions provider)
    {
        options.Authority = provider.Authority.TrimEnd('/');
        options.RequireHttpsMetadata = provider.RequireHttpsMetadata;
        options.MapInboundClaims = false;
        options.TokenValidationParameters.NameClaimType = "name";
        options.TokenValidationParameters.RoleClaimType = ClaimTypes.Role;
        options.TokenValidationParameters.ValidateAudience = provider.ValidateAudience;
        if (provider.ValidateAudience)
        {
            options.Audience = provider.Audience;
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
