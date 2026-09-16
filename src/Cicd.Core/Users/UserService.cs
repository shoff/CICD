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

    public Task<List<User>> ListAsync(CancellationToken cancellationToken) =>
        db.Users.AsNoTracking().OrderBy(u => u.DisplayName).ThenBy(u => u.Email).ToListAsync(cancellationToken);

    public Task<User?> FindAsync(Guid id, CancellationToken cancellationToken) =>
        db.Users.FirstOrDefaultAsync(u => u.Id == id, cancellationToken);

    /// <summary>Changes a role. Throws when the caller targets themselves or the change would leave no active admin.</summary>
    public async Task<User?> SetRoleAsync(Guid id, UserRole role, Guid? actingUserId, CancellationToken cancellationToken)
    {
        var user = await FindAsync(id, cancellationToken);
        if (user is null)
        {
            return null;
        }
        if (user.Id == actingUserId)
        {
            throw new InvalidOperationException("You cannot change your own role.");
        }
        if (user.Role == UserRole.Admin && role != UserRole.Admin && !user.Disabled)
        {
            await EnsureAnotherActiveAdminAsync(user.Id, cancellationToken);
        }
        user.Role = role;
        await db.SaveChangesAsync(cancellationToken);
        return user;
    }

    /// <summary>Disables or re-enables a user. Throws when the caller targets themselves or would disable the last active admin.</summary>
    public async Task<User?> SetDisabledAsync(Guid id, bool disabled, Guid? actingUserId, CancellationToken cancellationToken)
    {
        var user = await FindAsync(id, cancellationToken);
        if (user is null)
        {
            return null;
        }
        if (user.Id == actingUserId)
        {
            throw new InvalidOperationException("You cannot disable your own account.");
        }
        if (disabled && user.Role == UserRole.Admin && !user.Disabled)
        {
            await EnsureAnotherActiveAdminAsync(user.Id, cancellationToken);
        }
        user.Disabled = disabled;
        await db.SaveChangesAsync(cancellationToken);
        return user;
    }

    private async Task EnsureAnotherActiveAdminAsync(Guid exceptUserId, CancellationToken cancellationToken)
    {
        if (!await db.Users.AnyAsync(u => u.Id != exceptUserId && u.Role == UserRole.Admin && !u.Disabled, cancellationToken))
        {
            throw new InvalidOperationException("At least one active admin must remain.");
        }
    }

    private bool IsBootstrapAdmin(string? email) =>
        email is not null && security.Value.BootstrapAdmins.Any(a => string.Equals(a, email, StringComparison.OrdinalIgnoreCase));
}
