using System.Net.Http.Json;
using System.Text.Json;
using Cicd.Core.Users;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Cicd.Server.Security;

/// <summary>How a sign-in attempt ended. Only the user can fix <see cref="InvalidCredentials"/>.</summary>
public enum LoginOutcome
{
    Success,
    InvalidCredentials,
    ProviderUnavailable,
}

/// <summary>The outcome of a v21 sign-in. <see cref="Identity"/> is set only for <see cref="LoginOutcome.Success"/>.</summary>
public sealed record LoginResult(LoginOutcome Outcome, ExternalIdentity? Identity)
{
    public static LoginResult Invalid { get; } = new(LoginOutcome.InvalidCredentials, null);
    public static LoginResult Unavailable { get; } = new(LoginOutcome.ProviderUnavailable, null);
}

/// <summary>
/// Signs a user in with username and password against the IdP's v21 login endpoint and returns the identity carried by
/// the access token it issues. The token is read, not signature-validated: it is fetched directly from the IdP over
/// HTTPS in the same request, so its origin is already established. Nothing else from the response is kept.
/// </summary>
public sealed class V21LoginClient(HttpClient http, IOptionsMonitor<IdentityProviderOptions> options, ILogger<V21LoginClient> logger)
{
    private static readonly JsonSerializerOptions PascalCase = new() { PropertyNamingPolicy = null };
    private static readonly string[] TokenProperties = ["access_token", "accessToken", "AccessToken", "token", "Token"];

    /// <summary>
    /// Never throws for anything the IdP does: a rejected credential is <see cref="LoginOutcome.InvalidCredentials"/>,
    /// an unreachable provider or a response we cannot read is <see cref="LoginOutcome.ProviderUnavailable"/>. A
    /// cancellation the caller asked for still propagates.
    /// </summary>
    public async Task<LoginResult> LoginAsync(string username, string password, CancellationToken cancellationToken)
    {
        var settings = options.CurrentValue;
        var url = settings.EffectiveLoginUrl;
        if (settings.RequireHttpsMetadata && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("IdentityProvider:LoginUrl must use https unless RequireHttpsMetadata is false.");
        }
        var payload = new { UserName = username, Password = password, settings.ReturnUrl };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(settings.TimeoutSeconds));
        JsonElement json;
        try
        {
            using var response = await http.PostAsJsonAsync(url, payload, PascalCase, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Identity provider rejected sign-in for {User} with {Status}", username, (int)response.StatusCode);
                return LoginResult.Invalid;
            }
            json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: timeout.Token);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException
            || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            logger.LogWarning("Identity provider could not be reached for {User}: {Reason}", username, ex.Message);
            return LoginResult.Unavailable;
        }
        var token = FirstString(json, TokenProperties);
        if (string.IsNullOrWhiteSpace(token))
        {
            logger.LogWarning("Identity provider sign-in response for {User} carried no access token", username);
            return LoginResult.Unavailable;
        }
        ExternalIdentity? identity;
        try
        {
            identity = FromToken(token, settings.Authority);
        }
        catch (Exception ex) when (ex is ArgumentException or SecurityTokenException)
        {
            logger.LogWarning("Identity provider access token for {User} is not a readable JWT: {Reason}", username, ex.Message);
            return LoginResult.Unavailable;
        }
        if (identity is null)
        {
            logger.LogWarning("Identity provider access token for {User} has no 'sub' claim", username);
            return LoginResult.Unavailable;
        }
        return new LoginResult(LoginOutcome.Success, identity);
    }

    /// <summary>Reads the identity claims from an IdP JWT. Public so tests can cover the mapping without HTTP.</summary>
    public static ExternalIdentity? FromToken(string token, string fallbackIssuer) =>
        ExternalIdentityClaims.From(new JsonWebToken(token).Claims, fallbackIssuer);

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
