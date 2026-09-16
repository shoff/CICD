using Cicd.Contracts;
using Cicd.Core.Entities;
using Cicd.Core.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Cicd.Core.Users;

/// <summary>Identity as asserted by the OIDC provider. Subject is stable; the other fields are refreshed on every sign-in.</summary>
public sealed record ExternalIdentity(string Issuer, string Subject, string? Username, string? Email, string? DisplayName);

public sealed class UserService(CicdDbContext db, IOptions<SecurityOptions> security, TimeProvider clock)
{
    /// <summary>How often LastSeenAt is written for an otherwise unchanged user.</summary>
    public static readonly TimeSpan LastSeenResolution = TimeSpan.FromMinutes(5);

    /// <summary>Finds or creates the local user for an IdP identity. New users are viewers unless their email is a bootstrap admin.</summary>
    public async Task<User> EnsureUserAsync(ExternalIdentity identity, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var user = await db.Users.FirstOrDefaultAsync(u => u.Issuer == identity.Issuer && u.Subject == identity.Subject, cancellationToken);
        if (user is null)
        {
            user = new User { Issuer = identity.Issuer, Subject = identity.Subject, CreatedAt = now };
            db.Users.Add(user);
        }
        user.Username = identity.Username ?? user.Username;
        user.Email = identity.Email ?? user.Email;
        user.DisplayName = identity.DisplayName ?? user.DisplayName;
        if (IsBootstrapAdmin(user.Email))
        {
            user.Role = UserRole.Admin;
        }
        if (user.LastSeenAt is null || now - user.LastSeenAt.Value >= LastSeenResolution)
        {
            user.LastSeenAt = now;
        }
        if (db.ChangeTracker.HasChanges())
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        return user;
    }

    private bool IsBootstrapAdmin(string? email) =>
        email is not null && security.Value.BootstrapAdmins.Any(a => string.Equals(a, email, StringComparison.OrdinalIgnoreCase));
}
