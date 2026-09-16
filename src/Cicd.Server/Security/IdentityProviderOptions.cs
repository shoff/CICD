namespace Cicd.Server.Security;

/// <summary>The company identity provider. Browser sign-in posts credentials to its v21 login endpoint; API bearer tokens are validated against its discovery document.</summary>
public sealed class IdentityProviderOptions
{
    public const string SectionName = "IdentityProvider";
    /// <summary>Issuer base URL. Empty means no login: open mode unless Security:ApiToken is set.</summary>
    public string Authority { get; set; } = "";
    /// <summary>Custom username/password endpoint. Empty derives {Authority}/api/v21/accountv21/login.</summary>
    public string LoginUrl { get; set; } = "";
    /// <summary>Sent to the login endpoint as ReturnUrl; it requires a value but CICD does not use it.</summary>
    public string ReturnUrl { get; set; } = "http://localhost:7003/callback.html";
    public bool ValidateAudience { get; set; }
    public string Audience { get; set; } = "";
    public bool RequireHttpsMetadata { get; set; } = true;
    public int TimeoutSeconds { get; set; } = 15;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Authority);
    public string EffectiveLoginUrl => string.IsNullOrWhiteSpace(LoginUrl) ? $"{Authority.TrimEnd('/')}/api/v21/accountv21/login" : LoginUrl;
}
