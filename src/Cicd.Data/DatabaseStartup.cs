using Cicd.Core.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Cicd.Data;

/// <summary>Work that must happen before the host is built: migrate, then seed the settings table.</summary>
public static class DatabaseStartup
{
    public static async Task MigrateAsync(string connectionString, CancellationToken cancellationToken = default)
    {
        await using var db = Create(connectionString, NullSecretProtector.Instance);
        await db.Database.MigrateAsync(cancellationToken);
    }

    public static async Task<int> SeedSettingsAsync(string connectionString, IConfiguration configuration, ISecretProtector protector, CancellationToken cancellationToken = default)
    {
        await using var db = Create(connectionString, protector);
        var existing = await db.Settings.Select(s => s.Key).ToListAsync(cancellationToken);
        var rows = SettingsSeeder.MissingRows(configuration, existing, protector, TimeProvider.System);
        if (rows.Count == 0)
        {
            return 0;
        }
        db.Settings.AddRange(rows);
        await db.SaveChangesAsync(cancellationToken);
        return rows.Count;
    }

    /// <summary>
    /// Rewrites VCS root rows whose properties are still plaintext JSON, so credentials written before this feature
    /// end up under the key ring. Reading a legacy row succeeds because <see cref="SecretCodec"/> passes unprefixed
    /// values through; writing it back always encodes.
    /// </summary>
    public static async Task<int> EncryptLegacyVcsRootsAsync(string connectionString, ISecretProtector protector, CancellationToken cancellationToken = default)
    {
        await using var db = Create(connectionString, protector);
        var ids = await db.Database
            .SqlQueryRaw<Guid>("select id as \"Value\" from vcs_roots where properties not like 'enc:v1:%'")
            .ToListAsync(cancellationToken);
        if (ids.Count == 0)
        {
            return 0;
        }
        foreach (var id in ids)
        {
            var root = await db.VcsRoots.FirstAsync(v => v.Id == id, cancellationToken);
            // The value read back equals the value we would write, so the change tracker sees nothing: force the update.
            db.Entry(root).Property(r => r.Properties).IsModified = true;
        }
        await db.SaveChangesAsync(cancellationToken);
        return ids.Count;
    }

    /// <summary>Names of the VCS roots whose credentials the current key ring cannot decrypt, for the startup log.</summary>
    public static async Task<IReadOnlyList<string>> UnreadableVcsRootsAsync(string connectionString, ISecretProtector protector, CancellationToken cancellationToken = default)
    {
        await using var db = Create(connectionString, protector);
        return await Cicd.Core.Persistence.ProtectedJson.UnreadableVcsRootsAsync(db, protector, cancellationToken);
    }

    private static PostgresCicdDbContext Create(string connectionString, ISecretProtector protector) =>
        new(PostgresServiceCollectionExtensions.Configure(new DbContextOptionsBuilder<PostgresCicdDbContext>(), connectionString).Options, protector);
}
