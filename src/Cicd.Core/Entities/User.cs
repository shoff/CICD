using Cicd.Contracts;

namespace Cicd.Core.Entities;

/// <summary>A person known to CICD. Identity comes from the OIDC provider; the role is assigned here.</summary>
public sealed class User
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Issuer { get; set; }
    public required string Subject { get; set; }
    public string? Username { get; set; }
    public string? Email { get; set; }
    public string? DisplayName { get; set; }
    public UserRole Role { get; set; } = UserRole.Viewer;
    public bool Disabled { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastSeenAt { get; set; }
}
