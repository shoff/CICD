namespace Cicd.Core.Settings;

/// <summary>Encrypts values stored at rest. The Server supplies a Data Protection implementation; tests use the null one.</summary>
public interface ISecretProtector
{
    string Protect(string plaintext);
    string Unprotect(string ciphertext);
}

/// <summary>Identity protector for tests and design-time tooling. Never register it in a real server.</summary>
public sealed class NullSecretProtector : ISecretProtector
{
    public static NullSecretProtector Instance { get; } = new();
    public string Protect(string plaintext) => plaintext;
    public string Unprotect(string ciphertext) => ciphertext;
}
