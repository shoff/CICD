namespace Cicd.Core.Users;

public sealed class SecurityOptions
{
    public const string SectionName = "Security";
    /// <summary>Static bearer token that grants the admin role. For automation and break-glass use. Empty disables it.</summary>
    public string ApiToken { get; set; } = "";
    /// <summary>Emails promoted to admin every time they sign in. Never demotes anyone.</summary>
    public List<string> BootstrapAdmins { get; set; } = [];
}
