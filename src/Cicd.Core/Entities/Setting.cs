namespace Cicd.Core.Entities;

/// <summary>One managed configuration value. Secrets are stored in the SecretCodec wire format.</summary>
public sealed class Setting
{
    public required string Key { get; set; }
    public string Value { get; set; } = "";
    public bool IsSecret { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public string? UpdatedBy { get; set; }
}
