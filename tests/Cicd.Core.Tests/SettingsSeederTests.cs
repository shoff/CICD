using Cicd.Core.Settings;
using Microsoft.Extensions.Configuration;

namespace Cicd.Core.Tests;

public class SettingsSeederTests
{
    private static IConfiguration Config(params (string Key, string Value)[] pairs) =>
        new ConfigurationBuilder().AddInMemoryCollection(pairs.Select(p => new KeyValuePair<string, string?>(p.Key, p.Value))).Build();

    [Fact]
    public void Seeds_only_cataloged_keys_that_are_missing()
    {
        var config = Config(("Server:PublicUrl", "http://x"), ("GitHub:Token", "ghp"), ("ConnectionStrings:Cicd", "Host=..."));
        var rows = SettingsSeeder.MissingRows(config, existingKeys: ["Server:PublicUrl"], new ReversingProtector(), new TestClock());
        var keys = rows.Select(r => r.Key).ToList();
        Assert.DoesNotContain("Server:PublicUrl", keys);
        Assert.Contains("GitHub:Token", keys);
        Assert.DoesNotContain("ConnectionStrings:Cicd", keys);
        var token = rows.Single(r => r.Key == "GitHub:Token");
        Assert.True(token.IsSecret);
        Assert.Equal(SecretCodec.Encode(new ReversingProtector(), "ghp"), token.Value);
        Assert.Equal("seed", token.UpdatedBy);
    }

    [Fact]
    public void Missing_configuration_seeds_an_empty_value_and_arrays_are_joined()
    {
        var config = Config(("Security:BootstrapAdmins:0", "a@x.com"), ("Security:BootstrapAdmins:1", "b@x.com"));
        var rows = SettingsSeeder.MissingRows(config, [], new ReversingProtector(), new TestClock());
        Assert.Equal("a@x.com,b@x.com", rows.Single(r => r.Key == "Security:BootstrapAdmins").Value);
        Assert.Equal("", rows.Single(r => r.Key == "GitHub:ApiBaseUrl").Value);
        Assert.Equal(SettingsCatalog.All.Count, rows.Count);
    }
}
