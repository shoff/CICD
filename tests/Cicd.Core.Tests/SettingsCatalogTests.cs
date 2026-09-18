using Cicd.Core.Settings;

namespace Cicd.Core.Tests;

public class SettingsCatalogTests
{
    [Fact]
    public void Keys_are_unique_and_sectioned()
    {
        var keys = SettingsCatalog.All.Select(d => d.Key).ToList();
        Assert.Equal(keys.Count, keys.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(SettingsCatalog.All, d => Assert.StartsWith(d.Section + ":", d.Key));
    }

    [Fact]
    public void Secrets_and_restart_keys_are_as_designed()
    {
        Assert.Equal(SettingKind.Secret, SettingsCatalog.Find("GitHub:Token")!.Kind);
        Assert.Equal(SettingKind.Secret, SettingsCatalog.Find("Agents:AuthToken")!.Kind);
        Assert.Equal(SettingKind.List, SettingsCatalog.Find("Security:BootstrapAdmins")!.Kind);
        Assert.True(SettingsCatalog.Find("IdentityProvider:Authority")!.RestartRequired);
        Assert.False(SettingsCatalog.Find("Server:PublicUrl")!.RestartRequired);
        Assert.Null(SettingsCatalog.Find("ConnectionStrings:Cicd"));
        Assert.Null(SettingsCatalog.Find("Server:DataDirectory"));
    }

    [Fact]
    public void Every_number_and_boolean_has_a_default_the_binder_can_read()
    {
        // The seeder and the configuration mapper fall back to it, so it must exist and be valid itself.
        var typed = SettingsCatalog.All.Where(d => d.Kind is SettingKind.Number or SettingKind.Boolean);
        Assert.All(typed, d => Assert.True(d.DefaultValue is not null && d.Accepts(d.DefaultValue), d.Key));
    }

    [Fact]
    public void Find_is_case_insensitive() => Assert.NotNull(SettingsCatalog.Find("github:token"));
}
