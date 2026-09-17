using System.Text.Json;
using Cicd.Core.Entities;
using Cicd.Core.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Cicd.Core.Persistence;

public class CicdDbContext(DbContextOptions options, ISecretProtector protector) : DbContext(options)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Protects values that are encrypted at rest (VCS root properties, secret settings).</summary>
    public ISecretProtector Protector { get; } = protector;

    public DbSet<Project> Projects => Set<Project>();
    public DbSet<VcsRoot> VcsRoots => Set<VcsRoot>();
    public DbSet<BuildConfiguration> BuildConfigurations => Set<BuildConfiguration>();
    public DbSet<Build> Builds => Set<Build>();
    public DbSet<BuildLogLine> BuildLogLines => Set<BuildLogLine>();
    public DbSet<BuildArtifact> BuildArtifacts => Set<BuildArtifact>();
    public DbSet<Agent> Agents => Set<Agent>();
    public DbSet<PullRequest> PullRequests => Set<PullRequest>();
    public DbSet<TriggerStateEntry> TriggerState => Set<TriggerStateEntry>();
    public DbSet<User> Users => Set<User>();
    public DbSet<Setting> Settings => Set<Setting>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Project>(entity =>
        {
            entity.ToTable("projects");
            entity.HasIndex(p => p.Name).IsUnique();
            entity.HasOne(p => p.Parent).WithMany().HasForeignKey(p => p.ParentId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<VcsRoot>(entity =>
        {
            entity.ToTable("vcs_roots");
            entity.HasOne(v => v.Project).WithMany(p => p.VcsRoots).HasForeignKey(v => v.ProjectId).OnDelete(DeleteBehavior.Cascade);
            entity.Property(v => v.Properties).HasProtectedJsonConversion(Protector);
        });

        modelBuilder.Entity<BuildConfiguration>(entity =>
        {
            entity.ToTable("build_configurations");
            entity.HasIndex(c => new { c.ProjectId, c.Name }).IsUnique();
            entity.HasOne(c => c.Project).WithMany(p => p.BuildConfigurations).HasForeignKey(c => c.ProjectId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(c => c.VcsRoot).WithMany().HasForeignKey(c => c.VcsRootId).OnDelete(DeleteBehavior.SetNull);
            entity.Property(c => c.Steps).HasJsonConversion();
            entity.Property(c => c.Parameters).HasJsonConversion();
            entity.Property(c => c.Triggers).HasJsonConversion();
            entity.Property(c => c.AgentRequirements).HasJsonConversion();
            entity.Property(c => c.ArtifactPaths).HasJsonConversion();
            entity.Property(c => c.PullRequests).HasJsonConversion();
            entity.Property(c => c.BuildCounter).IsConcurrencyToken();
        });

        modelBuilder.Entity<Build>(entity =>
        {
            entity.ToTable("builds");
            entity.HasIndex(b => new { b.BuildConfigurationId, b.QueuedAt });
            entity.HasIndex(b => b.Status);
            entity.HasOne(b => b.BuildConfiguration).WithMany().HasForeignKey(b => b.BuildConfigurationId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(b => b.Agent).WithMany().HasForeignKey(b => b.AgentId).OnDelete(DeleteBehavior.SetNull);
            entity.HasOne(b => b.PullRequest).WithMany().HasForeignKey(b => b.PullRequestId).OnDelete(DeleteBehavior.SetNull);
            entity.Property(b => b.StepRuns).HasJsonConversion();
        });

        modelBuilder.Entity<BuildLogLine>(entity =>
        {
            entity.ToTable("build_log_lines");
            entity.HasIndex(l => new { l.BuildId, l.Sequence }).IsUnique();
            entity.HasOne<Build>().WithMany().HasForeignKey(l => l.BuildId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<BuildArtifact>(entity =>
        {
            entity.ToTable("build_artifacts");
            entity.HasIndex(a => a.BuildId);
            entity.HasOne<Build>().WithMany().HasForeignKey(a => a.BuildId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Agent>(entity =>
        {
            entity.ToTable("agents");
            entity.HasIndex(a => a.Name).IsUnique();
            entity.Property(a => a.Capabilities).HasJsonConversion();
        });

        modelBuilder.Entity<PullRequest>(entity =>
        {
            entity.ToTable("pull_requests");
            entity.HasIndex(p => new { p.VcsRootId, p.Number }).IsUnique();
            entity.HasOne(p => p.VcsRoot).WithMany().HasForeignKey(p => p.VcsRootId).OnDelete(DeleteBehavior.Cascade);
            entity.Property(p => p.LastBuiltShaByConfiguration).HasJsonConversion();
        });

        modelBuilder.Entity<TriggerStateEntry>(entity =>
        {
            entity.ToTable("trigger_state");
            entity.HasKey(t => new { t.BuildConfigurationId, t.TriggerIndex, t.Key });
        });

        modelBuilder.Entity<User>(entity =>
        {
            entity.ToTable("users");
            entity.HasIndex(u => new { u.Issuer, u.Subject }).IsUnique();
            entity.HasIndex(u => u.Email);
            entity.Property(u => u.Role).HasConversion<string>().HasMaxLength(16);
        });

        modelBuilder.Entity<Setting>(entity =>
        {
            entity.ToTable("settings");
            entity.HasKey(s => s.Key);
            entity.Property(s => s.Key).HasMaxLength(200);
        });
    }

    internal static string Serialize<T>(T value) => JsonSerializer.Serialize(value, JsonOptions);
    internal static T Deserialize<T>(string json) where T : new() => JsonSerializer.Deserialize<T>(json, JsonOptions) ?? new T();
}

/// <summary>EF caches one model per context type. The protected-JSON converter captures the protector, so the cache key must include it.</summary>
public sealed class ProtectorModelCacheKeyFactory : IModelCacheKeyFactory
{
    public object Create(DbContext context, bool designTime) =>
        (context.GetType(), designTime, context is CicdDbContext cicd ? cicd.Protector : null);
}

/// <summary>
/// Decoding for the protected JSON columns. EF value converters must be expression trees over static methods, so the
/// try/catch lives here rather than inline in the converter lambda.
/// </summary>
public static class ProtectedJson
{
    /// <summary>
    /// Never throws. A row whose ciphertext cannot be read (a lost or rotated key ring) loads as an empty dictionary,
    /// so the entity is still usable and the caller can report the missing credentials. See
    /// <see cref="Cicd.Core.Builds.BuildJobFactory"/>, which logs a warning when a checkout has no properties.
    /// </summary>
    public static Dictionary<string, string> DecodeProperties(ISecretProtector protector, string stored)
    {
        try
        {
            return CicdDbContext.Deserialize<Dictionary<string, string>>(SecretCodec.Decode(protector, stored));
        }
        catch (Exception)
        {
            return [];
        }
    }
}

internal static class JsonPropertyExtensions
{
    /// <summary>
    /// Stores the property as a JSON document. The column type ("jsonb") is applied by the PostgreSQL configuration in
    /// Cicd.Data, so this context stays provider-neutral for tests.
    /// </summary>
    public static Microsoft.EntityFrameworkCore.Metadata.Builders.PropertyBuilder<T> HasJsonConversion<T>(
        this Microsoft.EntityFrameworkCore.Metadata.Builders.PropertyBuilder<T> builder) where T : class, new()
    {
        var converter = new ValueConverter<T, string>(
            value => CicdDbContext.Serialize(value),
            json => CicdDbContext.Deserialize<T>(json));
        var comparer = new ValueComparer<T>(
            (left, right) => CicdDbContext.Serialize(left!) == CicdDbContext.Serialize(right!),
            value => CicdDbContext.Serialize(value).GetHashCode(),
            value => CicdDbContext.Deserialize<T>(CicdDbContext.Serialize(value)));
        builder.HasConversion(converter, comparer);
        builder.HasAnnotation("Cicd:Json", true);
        return builder;
    }

    /// <summary>Stores the dictionary as JSON encrypted with the protector. Plain JSON rows (no prefix) still load.</summary>
    public static Microsoft.EntityFrameworkCore.Metadata.Builders.PropertyBuilder<Dictionary<string, string>> HasProtectedJsonConversion(
        this Microsoft.EntityFrameworkCore.Metadata.Builders.PropertyBuilder<Dictionary<string, string>> builder, ISecretProtector protector)
    {
        var converter = new ValueConverter<Dictionary<string, string>, string>(
            value => SecretCodec.Encode(protector, CicdDbContext.Serialize(value)),
            stored => ProtectedJson.DecodeProperties(protector, stored));
        var comparer = new ValueComparer<Dictionary<string, string>>(
            (left, right) => CicdDbContext.Serialize(left!) == CicdDbContext.Serialize(right!),
            value => CicdDbContext.Serialize(value).GetHashCode(),
            value => CicdDbContext.Deserialize<Dictionary<string, string>>(CicdDbContext.Serialize(value)));
        builder.HasConversion(converter, comparer);
        return builder;
    }
}
