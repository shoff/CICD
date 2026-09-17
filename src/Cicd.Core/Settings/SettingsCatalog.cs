namespace Cicd.Core.Settings;

public enum SettingKind
{
    Text,
    Number,
    Boolean,
    Secret,
    List,
}

/// <summary>One managed configuration key. Key is the configuration path; Section is its first segment.</summary>
public sealed record SettingDefinition(string Key, string Section, string DisplayName, string Description, SettingKind Kind, bool RestartRequired = false);

/// <summary>Every key the settings page, API and seeder manage. Anything not listed stays in appsettings.</summary>
public static class SettingsCatalog
{
    public static IReadOnlyList<SettingDefinition> All { get; } =
    [
        new("Server:PublicUrl", "Server", "Public URL", "Externally reachable base URL, used in commit status links.", SettingKind.Text),
        new("Server:DispatchIntervalSeconds", "Server", "Dispatch interval (s)", "How often queued builds are assigned to agents.", SettingKind.Number),
        new("Server:TriggerPollIntervalSeconds", "Server", "Trigger poll interval (s)", "How often VCS and other triggers are polled.", SettingKind.Number),
        new("Server:PullRequestPollIntervalSeconds", "Server", "Pull request poll interval (s)", "How often open pull requests are refreshed.", SettingKind.Number),
        new("Agents:AuthToken", "Agents", "Agent auth token", "Shared secret every agent presents when connecting.", SettingKind.Secret),
        new("Agents:AutoAuthorize", "Agents", "Auto-authorize agents", "Authorize new agents on first registration. Development only.", SettingKind.Boolean),
        new("Security:ApiToken", "Security", "Static API token", "Bearer token that acts as an admin. Empty disables it.", SettingKind.Secret),
        new("Security:BootstrapAdmins", "Security", "Bootstrap admins", "Emails promoted to admin on every sign-in.", SettingKind.List),
        new("IdentityProvider:Authority", "IdentityProvider", "Authority", "Issuer base URL. Empty means open mode.", SettingKind.Text, RestartRequired: true),
        new("IdentityProvider:LoginUrl", "IdentityProvider", "Login URL", "v21 login endpoint. Empty derives {Authority}/api/v21/accountv21/login.", SettingKind.Text),
        new("IdentityProvider:ReturnUrl", "IdentityProvider", "Return URL", "Sent to the login endpoint; it requires a value.", SettingKind.Text),
        new("IdentityProvider:ValidateAudience", "IdentityProvider", "Validate audience", "Validate the audience of API bearer tokens.", SettingKind.Boolean, RestartRequired: true),
        new("IdentityProvider:Audience", "IdentityProvider", "Audience", "Expected audience when validation is on.", SettingKind.Text, RestartRequired: true),
        new("IdentityProvider:RequireHttpsMetadata", "IdentityProvider", "Require HTTPS", "Refuse plain-http provider URLs.", SettingKind.Boolean, RestartRequired: true),
        new("IdentityProvider:TimeoutSeconds", "IdentityProvider", "Login timeout (s)", "Timeout for the sign-in call.", SettingKind.Number),
        new("GitHub:Token", "GitHub", "GitHub token", "Default token for pull request discovery and status publishing.", SettingKind.Secret),
        new("GitHub:WebhookSecret", "GitHub", "Webhook secret", "If set, webhooks must carry a valid X-Hub-Signature-256.", SettingKind.Secret),
        new("GitHub:ApiBaseUrl", "GitHub", "API base URL", "GitHub REST API base.", SettingKind.Text),
        new("Plugins:Disabled", "Plugins", "Disabled plugins", "Plugin ids to skip even if present on disk.", SettingKind.List, RestartRequired: true),
    ];

    public static SettingDefinition? Find(string key) => All.FirstOrDefault(d => string.Equals(d.Key, key, StringComparison.OrdinalIgnoreCase));
}
