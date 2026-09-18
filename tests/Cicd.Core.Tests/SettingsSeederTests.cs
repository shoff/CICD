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
    public void Booleans_seed_in_the_same_casing_the_service_writes()
    {
        var config = Config(("Agents:AutoAuthorize", "True"));
        var rows = SettingsSeeder.MissingRows(config, [], new ReversingProtector(), new TestClock());
        Assert.Equal("true", rows.Single(r => r.Key == "Agents:AutoAuthorize").Value);
        // Nothing in configuration, so the catalog default is stored - not an empty string the binder cannot read.
        Assert.Equal("false", rows.Single(r => r.Key == "IdentityProvider:ValidateAudience").Value);
    }

    [Fact]
    public void Keys_missing_from_configuration_seed_their_catalog_default()
    {
        var rows = SettingsSeeder.MissingRows(Config(), [], new ReversingProtector(), new TestClock());
        Assert.Equal("true", rows.Single(r => r.Key == "IdentityProvider:RequireHttpsMetadata").Value);
        Assert.Equal("15", rows.Single(r => r.Key == "IdentityProvider:TimeoutSeconds").Value);
        Assert.Equal("https://api.github.com/", rows.Single(r => r.Key == "GitHub:ApiBaseUrl").Value);
    }

    [Fact]
    public void Empty_or_unparsable_typed_values_seed_their_catalog_default()
    {
        // An empty environment variable (Server__DispatchIntervalSeconds=) reads as "", not null.
        var config = Config(("Server:DispatchIntervalSeconds", ""), ("IdentityProvider:TimeoutSeconds", "soon"), ("Agents:AutoAuthorize", "yes"));
        var rows = SettingsSeeder.MissingRows(config, [], new ReversingProtector(), new TestClock());
        Assert.Equal("2", rows.Single(r => r.Key == "Server:DispatchIntervalSeconds").Value);
        Assert.Equal("15", rows.Single(r => r.Key == "IdentityProvider:TimeoutSeconds").Value);
        Assert.Equal("false", rows.Single(r => r.Key == "Agents:AutoAuthorize").Value);
    }

    [Fact]
    public void Missing_configuration_seeds_an_empty_value_and_arrays_are_joined()
    {
        var config = Config(("Security:BootstrapAdmins:0", "a@x.com"), ("Security:BootstrapAdmins:1", "b@x.com"));
        var rows = SettingsSeeder.MissingRows(config, [], new ReversingProtector(), new TestClock());
        Assert.Equal("a@x.com,b@x.com", rows.Single(r => r.Key == "Security:BootstrapAdmins").Value);
        Assert.Equal("", rows.Single(r => r.Key == "IdentityProvider:Audience").Value);
        Assert.Equal(SettingsCatalog.All.Count, rows.Count);
    }
}
