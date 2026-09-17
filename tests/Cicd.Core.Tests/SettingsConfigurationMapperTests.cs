using Cicd.Core.Entities;
using Cicd.Core.Settings;

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
    public void Unreadable_secret_is_omitted_not_thrown()
    {
        var throwing = new ThrowingProtector();
        var rows = new[] { new Setting { Key = "GitHub:Token", Value = "enc:v1:garbage", IsSecret = true } };
        var config = SettingsConfigurationMapper.ToConfiguration(rows, throwing);
        Assert.False(config.ContainsKey("GitHub:Token"));
    }

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
