using System.Net.Http.Json;
using System.Text.Json;
using Cicd.Core.Users;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;

namespace Cicd.Server.Security;

/// <summary>
/// Signs a user in with username and password against the IdP's v21 login endpoint and returns the identity carried by
/// the access token it issues. The token is read, not signature-validated: it is fetched directly from the IdP over
/// HTTPS in the same request, so its origin is already established. Nothing else from the response is kept.
/// </summary>
public sealed class V21LoginClient(HttpClient http, IOptions<IdentityProviderOptions> options, ILogger<V21LoginClient> logger)
{
    private static readonly JsonSerializerOptions PascalCase = new() { PropertyNamingPolicy = null };
    private static readonly string[] TokenProperties = ["access_token", "accessToken", "AccessToken", "token", "Token"];

    /// <summary>Null when the credentials are rejected or the response carries no usable token.</summary>
    public async Task<ExternalIdentity?> LoginAsync(string username, string password, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var url = settings.EffectiveLoginUrl;
        if (settings.RequireHttpsMetadata && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("IdentityProvider:LoginUrl must use https unless RequireHttpsMetadata is false.");
        }
        http.Timeout = TimeSpan.FromSeconds(settings.TimeoutSeconds);
        var payload = new { UserName = username, Password = password, settings.ReturnUrl };
        using var response = await http.PostAsJsonAsync(url, payload, PascalCase, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("Identity provider rejected sign-in for {User} with {Status}", username, (int)response.StatusCode);
            return null;
        }
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cancellationToken);
        var token = FirstString(json, TokenProperties);
        if (string.IsNullOrWhiteSpace(token))
        {
            logger.LogWarning("Identity provider sign-in response for {User} carried no access token", username);
            return null;
        }
        var identity = FromToken(token, settings.Authority);
        if (identity is null)
        {
            logger.LogWarning("Identity provider access token for {User} has no 'sub' claim", username);
        }
        return identity;
    }

    /// <summary>Reads the identity claims from an IdP JWT. Public so tests can cover the mapping without HTTP.</summary>
    public static ExternalIdentity? FromToken(string token, string fallbackIssuer)
    {
        var jwt = new JsonWebToken(token);
        string? Claim(params string[] types) => types.Select(t => jwt.Claims.FirstOrDefault(c => c.Type == t)?.Value).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
        var subject = Claim("sub");
        if (subject is null)
        {
            return null;
        }
        return new ExternalIdentity(
            Claim("iss") ?? fallbackIssuer.TrimEnd('/'),
            subject,
            Claim("username", "unique_name", "preferred_username"),
            Claim("email"),
            Claim("name", "full_name"));
    }

    private static string? FirstString(JsonElement json, string[] properties)
    {
        if (json.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        foreach (var property in properties)
        {
            if (json.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String)
            {
                return value.GetString();
            }
        }
        return null;
    }
}
