using System.Security.Claims;
using System.Text.Encodings.Web;
using Cicd.Contracts;
using Cicd.Core.Users;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;

namespace Cicd.Server.Security;

public sealed class AgentsOptions
{
    public const string SectionName = "Agents";
    /// <summary>Shared secret agents present when connecting. Required.</summary>
    public string AuthToken { get; set; } = "";
    /// <summary>Authorize agents automatically on first registration. Convenient for local development only.</summary>
    public bool AutoAuthorize { get; set; }
}

public static class Roles
{
    public const string Agent = "agent";
    public const string Viewer = "viewer";
    public const string Developer = "developer";
    public const string Admin = "admin";

    public static string Of(UserRole role) => role switch
    {
        UserRole.Admin => Admin,
        UserRole.Developer => Developer,
        _ => Viewer,
    };
}

public static class Policies
{
    public const string Agent = "Agent";
    public const string Viewer = "Viewer";
    public const string Developer = "Developer";
    public const string Admin = "Admin";
}

/// <summary>
/// Bearer-token scheme with two static tokens: the agent token (role "agent") and the API token (role "admin").
/// Reads the token from the Authorization header or, for SignalR WebSocket connections, from the access_token query string.
/// </summary>
public sealed class TokenAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    IOptions<SecurityOptions> security,
    IOptions<AgentsOptions> agents,
    ILoggerFactory loggerFactory,
    UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, loggerFactory, encoder)
{
    public const string SchemeName = "Token";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var token = ExtractToken();
        if (string.IsNullOrEmpty(token))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        string? role = null;
        if (!string.IsNullOrEmpty(agents.Value.AuthToken) && FixedTimeEquals(token, agents.Value.AuthToken))
        {
            role = Roles.Agent;
        }
        else if (!string.IsNullOrEmpty(security.Value.ApiToken) && FixedTimeEquals(token, security.Value.ApiToken))
        {
            role = Roles.Admin;
        }
        if (role is null)
        {
            return Task.FromResult(AuthenticateResult.Fail("Invalid token"));
        }

        var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, role), new Claim(ClaimTypes.Role, role)], SchemeName);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }

    private string? ExtractToken() => SchemeSelector.PresentedToken(Request);

    private static bool FixedTimeEquals(string left, string right) =>
        System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(left), System.Text.Encoding.UTF8.GetBytes(right));
}

public static class SecurityServiceCollectionExtensions
{
    public static IServiceCollection AddCicdSecurity(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<AgentsOptions>(configuration.GetSection(AgentsOptions.SectionName));
        services.AddAuthentication(TokenAuthenticationHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, TokenAuthenticationHandler>(TokenAuthenticationHandler.SchemeName, null);
        services.Configure<OidcOptions>(configuration.GetSection(OidcOptions.SectionName));
        services.AddSingleton<IAuthorizationHandler, RoleRequirementHandler>();
        services.AddAuthorizationBuilder()
            .AddPolicy(Policies.Agent, policy => policy.RequireRole(Roles.Agent))
            .AddPolicy(Policies.Viewer, policy => policy.AddRequirements(new RoleRequirement(UserRole.Viewer)))
            .AddPolicy(Policies.Developer, policy => policy.AddRequirements(new RoleRequirement(UserRole.Developer)))
            .AddPolicy(Policies.Admin, policy => policy.AddRequirements(new RoleRequirement(UserRole.Admin)));
        return services;
    }
}
