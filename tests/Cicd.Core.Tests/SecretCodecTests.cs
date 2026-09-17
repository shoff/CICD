using Cicd.Core.Settings;

namespace Cicd.Core.Tests;

/// <summary>Reverses the string so tests can see that protection was applied and undone.</summary>
public sealed class ReversingProtector : ISecretProtector
{
    public string Protect(string plaintext) => new(plaintext.Reverse().ToArray());
    public string Unprotect(string ciphertext) => new(ciphertext.Reverse().ToArray());
}

public class SecretCodecTests
{
    private readonly ReversingProtector protector = new();

    [Fact]
    public void Encode_adds_the_prefix_and_decode_removes_it()
    {
        var encoded = SecretCodec.Encode(protector, "hunter2");
        Assert.StartsWith("enc:v1:", encoded);
        Assert.Equal("2retnuh", encoded["enc:v1:".Length..]);
        Assert.Equal("hunter2", SecretCodec.Decode(protector, encoded));
    }

    [Fact]
    public void Decode_passes_legacy_plaintext_through()
    {
        Assert.Equal("plain", SecretCodec.Decode(protector, "plain"));
        Assert.Equal("", SecretCodec.Decode(protector, ""));
    }

    [Fact]
    public void IsEncoded_only_for_prefixed_values()
    {
        Assert.True(SecretCodec.IsEncoded("enc:v1:abc"));
        Assert.False(SecretCodec.IsEncoded("abc"));
        Assert.False(SecretCodec.IsEncoded(null));
    }

    [Fact]
    public void Null_protector_is_identity_but_still_prefixes()
    {
        var encoded = SecretCodec.Encode(NullSecretProtector.Instance, "x");
        Assert.Equal("enc:v1:x", encoded);
        Assert.Equal("x", SecretCodec.Decode(NullSecretProtector.Instance, encoded));
    }
}
