namespace Cicd.Core.Settings;

/// <summary>Wire format for protected values: "enc:v1:" + ciphertext. Values without the prefix are legacy plaintext.</summary>
public static class SecretCodec
{
    public const string Prefix = "enc:v1:";

    public static string Encode(ISecretProtector protector, string plaintext) => Prefix + protector.Protect(plaintext);

    public static string Decode(ISecretProtector protector, string stored)
    {
        if (!IsEncoded(stored))
        {
            return stored;
        }
        return protector.Unprotect(stored[Prefix.Length..]);
    }

    public static bool IsEncoded(string? stored) => stored is not null && stored.StartsWith(Prefix, StringComparison.Ordinal);
}
