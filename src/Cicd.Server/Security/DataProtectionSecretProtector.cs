using Cicd.Core.Settings;
using Microsoft.AspNetCore.DataProtection;

namespace Cicd.Server.Security;

/// <summary>Secrets at rest, protected with the server's Data Protection key ring under one fixed purpose.</summary>
public sealed class DataProtectionSecretProtector(IDataProtectionProvider provider) : ISecretProtector
{
    public const string Purpose = "Cicd.Secrets";
    private readonly IDataProtector protector = provider.CreateProtector(Purpose);

    public string Protect(string plaintext) => protector.Protect(plaintext);
    public string Unprotect(string ciphertext) => protector.Unprotect(ciphertext);
}
