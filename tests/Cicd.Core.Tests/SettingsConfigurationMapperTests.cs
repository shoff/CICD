using Cicd.Core.Entities;
using Cicd.Core.Services;
using Cicd.Core.Settings;
using Cicd.Core.Users;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Cicd.Core.Tests;

public class SettingsConfigurationMapperTests
{
    private readonly ReversingProtector protector = new();

    [Fact]
    public void Plain_values_pass_through_and_secrets_are_decoded()
    {
        var rows = new[]
        {
            new Setting { Key = "Server:PublicUrl", Value = "https://ci.example" },
            new Setting { Key = "GitHub:Token", Value = SecretCodec.Encode(protector, "ghp_x"), IsSecret = true },
        };
        var config = SettingsConfigurationMapper.ToConfiguration(rows, protector);
        Assert.Equal("https://ci.example", config["Server:PublicUrl"]);
        Assert.Equal("ghp_x", config["GitHub:Token"]);
    }

    [Fact]
    public void Lists_expand_to_indexed_keys_and_empty_lists_expand_to_nothing()
    {
        var rows = new[]
        {
            new Setting { Key = "Security:BootstrapAdmins", Value = "a@x.com, b@x.com" },
            new Setting { Key = "Plugins:Disabled", Value = "" },
        };
        var config = SettingsConfigurationMapper.ToConfiguration(rows, protector);
        Assert.Equal("a@x.com", config["Security:BootstrapAdmins:0"]);
        Assert.Equal("b@x.com", config["Security:BootstrapAdmins:1"]);
        Assert.False(config.ContainsKey("Security:BootstrapAdmins"));
        Assert.DoesNotContain(config.Keys, k => k.StartsWith("Plugins:Disabled"));
    }

    [Fact]
    public void Unreadable_secret_shadows_lower_layers()
    {
        var throwing = new ThrowingProtector();
        var rows = new[] { new Setting { Key = "GitHub:Token", Value = "enc:v1:garbage", IsSecret = true } };
        var config = SettingsConfigurationMapper.ToConfiguration(rows, throwing);
        // Fail closed: the key is present, so the appsettings value cannot show through, and it is not empty,
        // because consumers read an empty token or webhook secret as "protection switched off".
        Assert.True(config.ContainsKey("GitHub:Token"));
        Assert.Equal(SettingsConfigurationMapper.UnreadableSecret, config["GitHub:Token"]);
        Assert.StartsWith("unreadable:", SettingsConfigurationMapper.UnreadableSecret);
        Assert.True(SettingsConfigurationMapper.UnreadableSecret.Length > 40);
    }

    [Theory]
    [InlineData("Server:DispatchIntervalSeconds", "", "2")]
    [InlineData("Server:DispatchIntervalSeconds", "soon", "2")]
    [InlineData("IdentityProvider:TimeoutSeconds", "-1", "15")]
    [InlineData("IdentityProvider:RequireHttpsMetadata", "", "true")]
    [InlineData("Agents:AutoAuthorize", "yes", "false")]
    public void A_typed_value_the_binder_cannot_read_maps_to_the_catalog_default(string key, string stored, string expected)
    {
        var config = SettingsConfigurationMapper.ToConfiguration([new Setting { Key = key, Value = stored }], protector);
        Assert.Equal(expected, config[key]);
    }

    [Fact]
    public void Cleared_list_shadows_every_index_the_lower_layers_define()
    {
        var rows = new[] { new Setting { Key = "Security:BootstrapAdmins", Value = "" } };
        var config = SettingsConfigurationMapper.ToConfiguration(
            rows, protector, Shadowed("0", "1"), null);
        Assert.True(config.ContainsKey("Security:BootstrapAdmins:0"));
        Assert.True(config.ContainsKey("Security:BootstrapAdmins:1"));
        Assert.Null(config["Security:BootstrapAdmins:0"]);
        Assert.Null(config["Security:BootstrapAdmins:1"]);
    }

    [Fact]
    public void Shrunk_list_shadows_only_the_indices_it_no_longer_fills()
    {
        var rows = new[] { new Setting { Key = "Security:BootstrapAdmins", Value = "new" } };
        var config = SettingsConfigurationMapper.ToConfiguration(
            rows, protector, Shadowed("0", "1"), null);
        Assert.Equal("new", config["Security:BootstrapAdmins:0"]);
        Assert.Null(config["Security:BootstrapAdmins:1"]);
        Assert.False(config.ContainsKey("Security:BootstrapAdmins:2"));
    }

    [Fact]
    public void Lower_layer_indices_are_shadowed_by_key_not_by_count()
    {
        // Security__BootstrapAdmins__5 in the environment: one child, but its key is not "0".
        var rows = new[] { new Setting { Key = "Security:BootstrapAdmins", Value = "new" } };
        var config = SettingsConfigurationMapper.ToConfiguration(rows, protector, Shadowed("0", "5"), null);
        Assert.Equal("new", config["Security:BootstrapAdmins:0"]);
        Assert.True(config.ContainsKey("Security:BootstrapAdmins:5"));
        Assert.Null(config["Security:BootstrapAdmins:5"]);
    }

    [Fact]
    public void A_cleared_list_wins_over_a_lower_configuration_layer()
    {
        var mapped = SettingsConfigurationMapper.ToConfiguration(
            [new Setting { Key = "Security:BootstrapAdmins", Value = "" }],
            protector,
            Shadowed("0", "7"),
            null);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection([
                new KeyValuePair<string, string?>("Security:BootstrapAdmins:0", "a@x.com"),
                new KeyValuePair<string, string?>("Security:BootstrapAdmins:7", "b@x.com")])
            .Add(new StubSource(mapped))
            .Build();
        // Bound the way the host binds it. The binder turns a shadowed (null) child into a null element, which the
        // options registration strips, so no filtering here: this asserts what UserService actually sees.
        var services = new ServiceCollection();
        services.AddSecurityOptions(configuration);
        var admins = services.BuildServiceProvider().GetRequiredService<IOptions<SecurityOptions>>().Value.BootstrapAdmins;
        Assert.Empty(admins);
    }

    private static Dictionary<string, IReadOnlyCollection<string>> Shadowed(params string[] childKeys) =>
        new(StringComparer.OrdinalIgnoreCase) { ["Security:BootstrapAdmins"] = childKeys };

    [Fact]
    public void Join_and_split_round_trip()
    {
        Assert.Equal("a,b", SettingsConfigurationMapper.JoinList([" a ", "", "b"]));
        Assert.Equal(["a", "b"], SettingsConfigurationMapper.SplitList(" a , ,b"));
        Assert.Empty(SettingsConfigurationMapper.SplitList(null));
    }
}

public sealed class ThrowingProtector : ISecretProtector
{
    public string Protect(string plaintext) => throw new InvalidOperationException("no key");
    public string Unprotect(string ciphertext) => throw new InvalidOperationException("no key");
}

/// <summary>An upper configuration layer fed straight from the mapper output, the way the database provider is.</summary>
public sealed class StubSource(IDictionary<string, string?> values) : ConfigurationProvider, IConfigurationSource
{
    public override void Load() => Data = new Dictionary<string, string?>(values, StringComparer.OrdinalIgnoreCase);

    public IConfigurationProvider Build(IConfigurationBuilder builder) => this;
}
