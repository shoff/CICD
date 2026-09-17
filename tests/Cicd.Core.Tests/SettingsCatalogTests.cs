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
    public void Find_is_case_insensitive() => Assert.NotNull(SettingsCatalog.Find("github:token"));
}
