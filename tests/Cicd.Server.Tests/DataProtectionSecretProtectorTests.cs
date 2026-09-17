using Cicd.Core.Settings;
using Cicd.Server.Security;
using Microsoft.AspNetCore.DataProtection;

namespace Cicd.Server.Tests;

public class DataProtectionSecretProtectorTests
{
    [Fact]
    public void Round_trips_and_ciphertext_differs_from_plaintext()
    {
        var protector = new DataProtectionSecretProtector(new EphemeralDataProtectionProvider());
        var encoded = SecretCodec.Encode(protector, "hunter2");
        Assert.DoesNotContain("hunter2", encoded);
        Assert.Equal("hunter2", SecretCodec.Decode(protector, encoded));
    }

    [Fact]
    public void Another_key_ring_cannot_read_it()
    {
        var a = new DataProtectionSecretProtector(new EphemeralDataProtectionProvider());
        var b = new DataProtectionSecretProtector(new EphemeralDataProtectionProvider());
        var encoded = SecretCodec.Encode(a, "x");
        Assert.ThrowsAny<Exception>(() => SecretCodec.Decode(b, encoded));
    }
}
