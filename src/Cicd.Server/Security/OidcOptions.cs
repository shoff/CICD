namespace Cicd.Server.Security;

/// <summary>Settings for the company OpenID Connect provider. When not configured the server runs in open mode.</summary>
public sealed class OidcOptions
{
    public const string SectionName = "Oidc";
    public string Authority { get; set; } = "";
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    public string Scopes { get; set; } = "openid profile email";
    /// <summary>Validate the audience of bearer JWTs. Off until the IdP has an API resource for CICD.</summary>
    public bool ValidateAudience { get; set; }
    public string Audience { get; set; } = "";
    public bool RequireHttpsMetadata { get; set; } = true;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Authority) && !string.IsNullOrWhiteSpace(ClientId);
}
