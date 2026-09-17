# Database-Backed Settings and Credentials at Rest Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Move all managed server configuration into a `settings` table edited from an Admin → Settings page, encrypt secrets and VCS root credentials at rest with Data Protection, and keep every existing configuration consumer working.

**Architecture:** A custom `IConfigurationProvider` in `Cicd.Data` reads the `settings` table and is layered last in the host configuration, so `IOptions<T>` / `IConfiguration` consumers are untouched. `Cicd.Core.Settings` holds the catalog of managed keys, the secret codec, the mapper from rows to configuration keys, the seeder and the `SettingsService` that validates, writes and triggers reload. `CicdDbContext` gets an `ISecretProtector` and encrypts `VcsRoot.Properties` through its value converter. Startup migrates and seeds before the host is built.

**Tech Stack:** ASP.NET Core 10, EF Core 10 + Npgsql, Microsoft.AspNetCore.DataProtection (shared framework), Blazor interactive server, xunit + SQLite in tests.

**Spec:** `docs/superpowers/specs/2026-09-16-database-settings-design.md`

## Global Constraints

- .NET 10; central package management (no version on any `PackageReference`); new Microsoft packages `10.0.12`.
- **Braces on every control statement** (`if`, `else`, `for`, `foreach`, `while`, `do`, `using`), even single-line bodies. Never run `dotnet format`; touch only the files the task names; stage by name (never `git add -A`/`.`).
- No leading underscores on C# fields; primary constructors; nullable enabled; explicit `IResult`/`Task<IResult>` on minimal-API lambdas.
- snake_case DB naming via `UseSnakeCaseNamingConvention()`; `CicdDbContext` in Core stays provider-neutral; migrations live in `Cicd.Data`.
- Migration command: `cd src/Cicd.Data && DOTNET_ROLL_FORWARD=LatestMajor dotnet ef migrations add <Name> --context PostgresCicdDbContext --output-dir Migrations`.
- Secret storage format: `enc:v1:<base64url ciphertext>`; values without the prefix are legacy plaintext.
- Data Protection: purpose `Cicd.Secrets`, application name `cicd`, key ring at `<Server:DataDirectory>/keys`.
- Managed keys are exactly the catalog in the spec. Not managed: `ConnectionStrings:*`, `Server:DataDirectory`, `Server:MigrateOnStartup`, `Plugins:Directory`, `Logging`, `AllowedHosts`, Kestrel/URLs.
- Server runs: always `ASPNETCORE_ENVIRONMENT=Development DOTNET_ENVIRONMENT=Development`; port 5000 must be free; kill what you start. PostgreSQL is the Docker container `cicd-postgres` (user/password/db `cicd`); inspect with `docker exec cicd-postgres psql -U cicd -d cicd -c "<sql>"`.
- Commit after every task with trailer `Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>`.
- Run from `/Users/stevenhoff/dev/CICD`. Tests: `dotnet test Cicd.slnx` (currently Core 37, Server 73).

## File map

| File | Responsibility |
| --- | --- |
| `src/Cicd.Core/Settings/ISecretProtector.cs` | `ISecretProtector`, `NullSecretProtector` |
| `src/Cicd.Core/Settings/SecretCodec.cs` | prefix handling around a protector |
| `src/Cicd.Core/Settings/SettingsCatalog.cs` | `SettingKind`, `SettingDefinition`, the catalog |
| `src/Cicd.Core/Entities/Setting.cs` | entity |
| `src/Cicd.Core/Persistence/CicdDbContext.cs` | protector ctor, `Settings` DbSet, protected `Properties` converter, model cache key (edit) |
| `src/Cicd.Core/Settings/SettingsConfigurationMapper.cs` | rows → configuration dictionary |
| `src/Cicd.Core/Settings/SettingsSeeder.cs` | missing rows from `IConfiguration` |
| `src/Cicd.Core/Settings/SettingsService.cs` | `ISettingsReloader`, `SettingView`, read/validate/write/restart-pending |
| `src/Cicd.Data/PostgresCicdDbContext.cs`, `DesignTimeDbContextFactory.cs` | protector ctor (edit) |
| `src/Cicd.Data/DatabaseStartup.cs` | pre-host migration |
| `src/Cicd.Data/DatabaseSettingsConfiguration.cs` | configuration source + provider |
| `src/Cicd.Data/Migrations/*_AddSettingsAndProtectVcsRootProperties.cs` | migration (generated, then hand-edited) |
| `src/Cicd.Server/Security/DataProtectionSecretProtector.cs` | Data Protection implementation |
| `src/Cicd.Server/Program.cs` | startup order (edit) |
| `src/Cicd.Server/Api/SettingsEndpoints.cs` | `GET/PUT /api/v1/settings` |
| `src/Cicd.Server/Components/Pages/Admin/Settings.razor` | settings page |
| `src/Cicd.Server/Components/Layout/MainLayout.razor`, `wwwroot/app.css` | Admin group (edit) |
| Consumers switched to `IOptionsMonitor` | `TokenAuthentication.cs`, `RoleRequirement.cs`, `UserService.cs`, `V21LoginClient.cs`, `AuthEndpoints.cs`, `MainLayout.razor`, `Login.razor`, `PullRequestStatusNotifier.cs`, `BuildDispatcher.cs`, `TriggerService.cs`, `PullRequestService.cs` |
| Tests | `tests/Cicd.Core.Tests/{SecretCodecTests,SettingsCatalogTests,VcsRootProtectionTests,SettingsConfigurationMapperTests,SettingsSeederTests,SettingsServiceTests}.cs`, `tests/Cicd.Server.Tests/DataProtectionSecretProtectorTests.cs`; `TestDb` in both test projects gains a protector parameter |
| Docs | `README.md`, `docs/ARCHITECTURE.md`, `HANDOFF.md` |

---

### Task 1: Secret protector, codec, `Setting` entity, catalog, protected VCS root properties, migration

**Files:**
- Create: `src/Cicd.Core/Settings/ISecretProtector.cs`, `SecretCodec.cs`, `SettingsCatalog.cs`, `src/Cicd.Core/Entities/Setting.cs`
- Modify: `src/Cicd.Core/Persistence/CicdDbContext.cs`, `src/Cicd.Data/PostgresCicdDbContext.cs`, `src/Cicd.Data/DesignTimeDbContextFactory.cs`, `src/Cicd.Data/PostgresServiceCollectionExtensions.cs`, `tests/Cicd.Core.Tests/TestDb.cs`, `tests/Cicd.Server.Tests/TestDb.cs`
- Create: `tests/Cicd.Core.Tests/SecretCodecTests.cs`, `SettingsCatalogTests.cs`, `VcsRootProtectionTests.cs`
- Generate: migration `AddSettingsAndProtectVcsRootProperties`

**Interfaces (produced):**
```csharp
namespace Cicd.Core.Settings;
public interface ISecretProtector { string Protect(string plaintext); string Unprotect(string ciphertext); }
public sealed class NullSecretProtector : ISecretProtector { public static NullSecretProtector Instance { get; } = new(); ... }
public static class SecretCodec { const string Prefix = "enc:v1:"; static string Encode(ISecretProtector, string); static string Decode(ISecretProtector, string); static bool IsEncoded(string?); }
public enum SettingKind { Text, Number, Boolean, Secret, List }
public sealed record SettingDefinition(string Key, string Section, string DisplayName, string Description, SettingKind Kind, bool RestartRequired = false);
public static class SettingsCatalog { static IReadOnlyList<SettingDefinition> All; static SettingDefinition? Find(string key); }
// Cicd.Core.Entities.Setting { string Key; string Value; bool IsSecret; DateTimeOffset UpdatedAt; string? UpdatedBy; }
// CicdDbContext(DbContextOptions options, ISecretProtector protector); DbSet<Setting> Settings; ISecretProtector Protector
// TestDb(ISecretProtector? protector = null)
```

- [ ] **Step 1: Write the failing tests**

`tests/Cicd.Core.Tests/SecretCodecTests.cs`:
```csharp
using Cicd.Core.Settings;

namespace Cicd.Core.Tests;

/// <summary>Reverses the string so tests can see that protection was applied and undone.</summary>
public sealed class ReversingProtector : ISecretProtector
{
    public string Protect(string plaintext) => new(plaintext.Reverse().ToArray());
    public string Unprotect(string ciphertext) => new(ciphertext.Reverse().ToArray());
}

public class SecretCodecTests
{
    private readonly ReversingProtector protector = new();

    [Fact]
    public void Encode_adds_the_prefix_and_decode_removes_it()
    {
        var encoded = SecretCodec.Encode(protector, "hunter2");
        Assert.StartsWith("enc:v1:", encoded);
        Assert.Equal("2retnuh", encoded["enc:v1:".Length..]);
        Assert.Equal("hunter2", SecretCodec.Decode(protector, encoded));
    }

    [Fact]
    public void Decode_passes_legacy_plaintext_through()
    {
        Assert.Equal("plain", SecretCodec.Decode(protector, "plain"));
        Assert.Equal("", SecretCodec.Decode(protector, ""));
    }

    [Fact]
    public void IsEncoded_only_for_prefixed_values()
    {
        Assert.True(SecretCodec.IsEncoded("enc:v1:abc"));
        Assert.False(SecretCodec.IsEncoded("abc"));
        Assert.False(SecretCodec.IsEncoded(null));
    }

    [Fact]
    public void Null_protector_is_identity_but_still_prefixes()
    {
        var encoded = SecretCodec.Encode(NullSecretProtector.Instance, "x");
        Assert.Equal("enc:v1:x", encoded);
        Assert.Equal("x", SecretCodec.Decode(NullSecretProtector.Instance, encoded));
    }
}
```

`tests/Cicd.Core.Tests/SettingsCatalogTests.cs`:
```csharp
using Cicd.Core.Settings;

namespace Cicd.Core.Tests;

public class SettingsCatalogTests
{
    [Fact]
    public void Keys_are_unique_and_sectioned()
    {
        var keys = SettingsCatalog.All.Select(d => d.Key).ToList();
        Assert.Equal(keys.Count, keys.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(SettingsCatalog.All, d => Assert.StartsWith(d.Section + ":", d.Key));
    }

    [Fact]
    public void Secrets_and_restart_keys_are_as_designed()
    {
        Assert.Equal(SettingKind.Secret, SettingsCatalog.Find("GitHub:Token")!.Kind);
        Assert.Equal(SettingKind.Secret, SettingsCatalog.Find("Agents:AuthToken")!.Kind);
        Assert.Equal(SettingKind.List, SettingsCatalog.Find("Security:BootstrapAdmins")!.Kind);
        Assert.True(SettingsCatalog.Find("IdentityProvider:Authority")!.RestartRequired);
        Assert.False(SettingsCatalog.Find("Server:PublicUrl")!.RestartRequired);
        Assert.Null(SettingsCatalog.Find("ConnectionStrings:Cicd"));
        Assert.Null(SettingsCatalog.Find("Server:DataDirectory"));
    }

    [Fact]
    public void Find_is_case_insensitive() => Assert.NotNull(SettingsCatalog.Find("github:token"));
}
```

`tests/Cicd.Core.Tests/VcsRootProtectionTests.cs`:
```csharp
using Cicd.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Cicd.Core.Tests;

public class VcsRootProtectionTests
{
    private static VcsRoot Root(Guid projectId) => new()
    {
        ProjectId = projectId, Name = "repo", ProviderId = "git", Url = "https://example.com/r.git",
        Properties = new Dictionary<string, string> { ["git.password"] = "hunter2", ["git.depth"] = "50" },
    };

    [Fact]
    public async Task Properties_are_stored_encoded_and_read_back_as_plaintext()
    {
        using var testDb = new TestDb(new ReversingProtector());
        Guid rootId;
        await using (var db = testDb.Create())
        {
            var project = new Project { Name = "p" };
            var root = Root(project.Id);
            db.Projects.Add(project);
            db.VcsRoots.Add(root);
            await db.SaveChangesAsync();
            rootId = root.Id;
        }
        await using (var db = testDb.Create())
        {
            var stored = await db.Database.SqlQueryRaw<string>("select properties as \"Value\" from vcs_roots").SingleAsync();
            Assert.StartsWith("enc:v1:", stored);
            Assert.DoesNotContain("hunter2", stored);
            var root = await db.VcsRoots.SingleAsync(r => r.Id == rootId);
            Assert.Equal("hunter2", root.Properties["git.password"]);
            Assert.Equal("50", root.Properties["git.depth"]);
        }
    }

    [Fact]
    public async Task Legacy_plaintext_json_still_loads()
    {
        using var testDb = new TestDb(new ReversingProtector());
        await using (var db = testDb.Create())
        {
            var project = new Project { Name = "p" };
            db.Projects.Add(project);
            await db.SaveChangesAsync();
            await db.Database.ExecuteSqlRawAsync(
                "insert into vcs_roots (id, project_id, name, provider_id, url, default_branch, properties, created_at) values ({0}, {1}, 'legacy', 'git', 'https://example.com/l.git', 'main', {2}, {3})",
                Guid.NewGuid(), project.Id, """{"git.password":"old"}""", DateTimeOffset.UtcNow);
        }
        await using (var db = testDb.Create())
        {
            var root = await db.VcsRoots.SingleAsync(r => r.Name == "legacy");
            Assert.Equal("old", root.Properties["git.password"]);
        }
    }
}
```

Update both `TestDb` helpers (Core and Server test projects) to accept a protector:
```csharp
    public TestDb(ISecretProtector? protector = null)
    {
        this.protector = protector ?? NullSecretProtector.Instance;
        connection.Open();
        options = new DbContextOptionsBuilder<CicdDbContext>().UseSqlite(connection).Options;
        using var db = Create();
        db.Database.EnsureCreated();
    }

    public CicdDbContext Create() => new(options, protector);
```
(add `private readonly ISecretProtector protector;` and `using Cicd.Core.Settings;`).

- [ ] **Step 2: Run to verify failure** — `dotnet test tests/Cicd.Core.Tests --filter "SecretCodecTests|SettingsCatalogTests|VcsRootProtectionTests"` → build errors for the missing types.

- [ ] **Step 3: Implement**

`src/Cicd.Core/Settings/ISecretProtector.cs`:
```csharp
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
```

`src/Cicd.Core/Settings/SecretCodec.cs`:
```csharp
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
```

`src/Cicd.Core/Settings/SettingsCatalog.cs`:
```csharp
namespace Cicd.Core.Settings;

public enum SettingKind
{
    Text,
    Number,
    Boolean,
    Secret,
    List,
}

/// <summary>One managed configuration key. Key is the configuration path; Section is its first segment.</summary>
public sealed record SettingDefinition(string Key, string Section, string DisplayName, string Description, SettingKind Kind, bool RestartRequired = false);

/// <summary>Every key the settings page, API and seeder manage. Anything not listed stays in appsettings.</summary>
public static class SettingsCatalog
{
    public static IReadOnlyList<SettingDefinition> All { get; } =
    [
        new("Server:PublicUrl", "Server", "Public URL", "Externally reachable base URL, used in commit status links.", SettingKind.Text),
        new("Server:DispatchIntervalSeconds", "Server", "Dispatch interval (s)", "How often queued builds are assigned to agents.", SettingKind.Number),
        new("Server:TriggerPollIntervalSeconds", "Server", "Trigger poll interval (s)", "How often VCS and other triggers are polled.", SettingKind.Number),
        new("Server:PullRequestPollIntervalSeconds", "Server", "Pull request poll interval (s)", "How often open pull requests are refreshed.", SettingKind.Number),
        new("Agents:AuthToken", "Agents", "Agent auth token", "Shared secret every agent presents when connecting.", SettingKind.Secret),
        new("Agents:AutoAuthorize", "Agents", "Auto-authorize agents", "Authorize new agents on first registration. Development only.", SettingKind.Boolean),
        new("Security:ApiToken", "Security", "Static API token", "Bearer token that acts as an admin. Empty disables it.", SettingKind.Secret),
        new("Security:BootstrapAdmins", "Security", "Bootstrap admins", "Emails promoted to admin on every sign-in.", SettingKind.List),
        new("IdentityProvider:Authority", "IdentityProvider", "Authority", "Issuer base URL. Empty means open mode.", SettingKind.Text, RestartRequired: true),
        new("IdentityProvider:LoginUrl", "IdentityProvider", "Login URL", "v21 login endpoint. Empty derives {Authority}/api/v21/accountv21/login.", SettingKind.Text),
        new("IdentityProvider:ReturnUrl", "IdentityProvider", "Return URL", "Sent to the login endpoint; it requires a value.", SettingKind.Text),
        new("IdentityProvider:ValidateAudience", "IdentityProvider", "Validate audience", "Validate the audience of API bearer tokens.", SettingKind.Boolean, RestartRequired: true),
        new("IdentityProvider:Audience", "IdentityProvider", "Audience", "Expected audience when validation is on.", SettingKind.Text, RestartRequired: true),
        new("IdentityProvider:RequireHttpsMetadata", "IdentityProvider", "Require HTTPS", "Refuse plain-http provider URLs.", SettingKind.Boolean, RestartRequired: true),
        new("IdentityProvider:TimeoutSeconds", "IdentityProvider", "Login timeout (s)", "Timeout for the sign-in call.", SettingKind.Number),
        new("GitHub:Token", "GitHub", "GitHub token", "Default token for pull request discovery and status publishing.", SettingKind.Secret),
        new("GitHub:WebhookSecret", "GitHub", "Webhook secret", "If set, webhooks must carry a valid X-Hub-Signature-256.", SettingKind.Secret),
        new("GitHub:ApiBaseUrl", "GitHub", "API base URL", "GitHub REST API base.", SettingKind.Text),
        new("Plugins:Disabled", "Plugins", "Disabled plugins", "Plugin ids to skip even if present on disk.", SettingKind.List, RestartRequired: true),
    ];

    public static SettingDefinition? Find(string key) => All.FirstOrDefault(d => string.Equals(d.Key, key, StringComparison.OrdinalIgnoreCase));
}
```

`src/Cicd.Core/Entities/Setting.cs`:
```csharp
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
```

`CicdDbContext.cs` changes:
1. Constructor and property: `public class CicdDbContext(DbContextOptions options, ISecretProtector protector) : DbContext(options)` with `public ISecretProtector Protector { get; } = protector;` and `using Cicd.Core.Settings;`.
2. `public DbSet<Setting> Settings => Set<Setting>();`
3. In `OnModelCreating`, replace `entity.Property(v => v.Properties).HasJsonConversion();` on `VcsRoot` with `entity.Property(v => v.Properties).HasProtectedJsonConversion(Protector);` and add:
```csharp
        modelBuilder.Entity<Setting>(entity =>
        {
            entity.ToTable("settings");
            entity.HasKey(s => s.Key);
            entity.Property(s => s.Key).HasMaxLength(200);
        });
```
4. Model cache key so the converter's captured protector is per model: override `OnConfiguring` is not needed; instead add to the class:
```csharp
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        base.ConfigureConventions(configurationBuilder);
    }
```
   is NOT the mechanism. Use a replaced service: in `PostgresServiceCollectionExtensions.Configure` and in `TestDb` add `.ReplaceService<IModelCacheKeyFactory, ProtectorModelCacheKeyFactory>()` to the options builder, with this class in `CicdDbContext.cs`:
```csharp
/// <summary>EF caches one model per context type. The protected-JSON converter captures the protector, so the cache key must include it.</summary>
public sealed class ProtectorModelCacheKeyFactory : IModelCacheKeyFactory
{
    public object Create(DbContext context, bool designTime) =>
        (context.GetType(), designTime, context is CicdDbContext cicd ? cicd.Protector : null);
}
```
   (`using Microsoft.EntityFrameworkCore.Infrastructure;`.)
5. Add the converter helper next to `HasJsonConversion` in `JsonPropertyExtensions`:
```csharp
    /// <summary>Stores the dictionary as JSON encrypted with the protector. Plain JSON rows (no prefix) still load.</summary>
    public static Microsoft.EntityFrameworkCore.Metadata.Builders.PropertyBuilder<Dictionary<string, string>> HasProtectedJsonConversion(
        this Microsoft.EntityFrameworkCore.Metadata.Builders.PropertyBuilder<Dictionary<string, string>> builder, ISecretProtector protector)
    {
        var converter = new ValueConverter<Dictionary<string, string>, string>(
            value => SecretCodec.Encode(protector, CicdDbContext.Serialize(value)),
            stored => CicdDbContext.Deserialize<Dictionary<string, string>>(SecretCodec.Decode(protector, stored)));
        var comparer = new ValueComparer<Dictionary<string, string>>(
            (left, right) => CicdDbContext.Serialize(left!) == CicdDbContext.Serialize(right!),
            value => CicdDbContext.Serialize(value).GetHashCode(),
            value => CicdDbContext.Deserialize<Dictionary<string, string>>(CicdDbContext.Serialize(value)));
        builder.HasConversion(converter, comparer);
        return builder;
    }
```
   No `Cicd:Json` annotation, so Postgres keeps it as `text`.

`PostgresCicdDbContext`: `public sealed class PostgresCicdDbContext(DbContextOptions<PostgresCicdDbContext> options, ISecretProtector protector) : CicdDbContext(options, protector)`. `DesignTimeDbContextFactory`: `new PostgresCicdDbContext(options.Options, NullSecretProtector.Instance)`. `PostgresServiceCollectionExtensions.Configure(DbContextOptionsBuilder, string)`: add `options.ReplaceService<IModelCacheKeyFactory, ProtectorModelCacheKeyFactory>();`. `AddCicdPostgres` already uses `AddDbContextFactory<PostgresCicdDbContext>`, which resolves `ISecretProtector` from DI (registered in Task 3); nothing else to change there. `TestDb`: `new DbContextOptionsBuilder<CicdDbContext>().UseSqlite(connection).ReplaceService<IModelCacheKeyFactory, ProtectorModelCacheKeyFactory>().Options`.

- [ ] **Step 4: Run the three test classes** → all pass. Then `dotnet build Cicd.slnx` — the Server will fail to compile until Task 3 registers `ISecretProtector`? No: DI resolution is runtime, so the build passes; `Program.cs` runtime would fail until Task 3. That is acceptable between tasks on this branch.

- [ ] **Step 5: Migration**

Run the migration command with name `AddSettingsAndProtectVcsRootProperties`. Open the generated `Up`: it will contain `CreateTable("settings", ...)` and an `AlterColumn` for `vcs_roots.properties` from `jsonb` to `text`. Replace that `AlterColumn` in `Up` with:
```csharp
            migrationBuilder.Sql("ALTER TABLE vcs_roots ALTER COLUMN properties TYPE text USING properties::text;");
```
and in `Down` with:
```csharp
            migrationBuilder.Sql("ALTER TABLE vcs_roots ALTER COLUMN properties TYPE jsonb USING properties::jsonb;");
```
(Keep the generated `CreateTable`/`DropTable`.) Check: `grep -n "USING\|settings" src/Cicd.Data/Migrations/*_AddSettingsAndProtectVcsRootProperties.cs`.

- [ ] **Step 6: Full build and tests, commit**

`dotnet build Cicd.slnx && dotnet test Cicd.slnx` → Core 37 + 9 = 46, Server 73 (its `TestDb` still compiles with the default protector). Commit: `Add secret protector, settings entity and catalog; encrypt VCS root properties`.

---

### Task 2: Mapper, seeder, `SettingsService`

**Files:**
- Create: `src/Cicd.Core/Settings/SettingsConfigurationMapper.cs`, `SettingsSeeder.cs`, `SettingsService.cs`
- Modify: `src/Cicd.Core/Services/CoreServiceCollectionExtensions.cs` (register `SettingsService` scoped)
- Create: `tests/Cicd.Core.Tests/SettingsConfigurationMapperTests.cs`, `SettingsSeederTests.cs`, `SettingsServiceTests.cs`

**Interfaces (produced):**
```csharp
namespace Cicd.Core.Settings;
public static class SettingsConfigurationMapper { static IDictionary<string, string?> ToConfiguration(IEnumerable<Setting> rows, ISecretProtector protector, ILogger? logger = null); static string JoinList(IEnumerable<string>); static IReadOnlyList<string> SplitList(string?); }
public static class SettingsSeeder { static IReadOnlyList<Setting> MissingRows(IConfiguration configuration, IEnumerable<string> existingKeys, ISecretProtector protector, TimeProvider clock); }
public interface ISettingsReloader { IReadOnlyDictionary<string, string?> ValuesAtStartup { get; } void Reload(); }
public sealed record SettingView(SettingDefinition Definition, string Value, bool IsSet, bool RestartPending, bool Unreadable);
public sealed class SettingsService(CicdDbContext db, ISecretProtector protector, ISettingsReloader reloader, TimeProvider clock, ILogger<SettingsService> logger)
{ Task<IReadOnlyList<SettingView>> GetAllAsync(ct); Task UpdateAsync(IReadOnlyDictionary<string, string?> values, string? updatedBy, ct); static IReadOnlyList<string> Validate(IReadOnlyDictionary<string, string?> values); }
```
Semantics: list values are stored comma-separated (`JoinList`/`SplitList` trim entries, drop empties) and expanded to `Key:0`, `Key:1`, … by the mapper; an empty list stores `""` and expands to nothing (so the binder gets an empty array). Secrets: the mapper decodes with `SecretCodec.Decode`; if `Unprotect` throws, the mapper logs an error naming the key and omits it. `GetAllAsync` masks secrets as `"********"` when set, `""` when empty, and marks `Unreadable` when decoding throws. `UpdateAsync` ignores secret entries whose value is `""` (unchanged), validates all entries first (unknown key; Number must be a non-negative integer; Boolean must be `true`/`false` case-insensitive), writes rows (encoding secrets), saves, then calls `reloader.Reload()`. `RestartPending` = definition is restart-required and the stored (decoded) value differs from `reloader.ValuesAtStartup[key]` (missing = `""`).

- [ ] **Step 1: Write the failing tests**

`SettingsConfigurationMapperTests.cs`:
```csharp
using Cicd.Core.Entities;
using Cicd.Core.Settings;

namespace Cicd.Core.Tests;

public class SettingsConfigurationMapperTests
{
    private readonly ReversingProtector protector = new();

    [Fact]
    public void Plain_values_pass_through_and_secrets_are_decoded()
    {
        var rows = new[]
        {
            new Setting { Key = "Server:PublicUrl", Value = "https://ci.example" },
            new Setting { Key = "GitHub:Token", Value = SecretCodec.Encode(protector, "ghp_x"), IsSecret = true },
        };
        var config = SettingsConfigurationMapper.ToConfiguration(rows, protector);
        Assert.Equal("https://ci.example", config["Server:PublicUrl"]);
        Assert.Equal("ghp_x", config["GitHub:Token"]);
    }

    [Fact]
    public void Lists_expand_to_indexed_keys_and_empty_lists_expand_to_nothing()
    {
        var rows = new[]
        {
            new Setting { Key = "Security:BootstrapAdmins", Value = "a@x.com, b@x.com" },
            new Setting { Key = "Plugins:Disabled", Value = "" },
        };
        var config = SettingsConfigurationMapper.ToConfiguration(rows, protector);
        Assert.Equal("a@x.com", config["Security:BootstrapAdmins:0"]);
        Assert.Equal("b@x.com", config["Security:BootstrapAdmins:1"]);
        Assert.False(config.ContainsKey("Security:BootstrapAdmins"));
        Assert.DoesNotContain(config.Keys, k => k.StartsWith("Plugins:Disabled"));
    }

    [Fact]
    public void Unreadable_secret_is_omitted_not_thrown()
    {
        var throwing = new ThrowingProtector();
        var rows = new[] { new Setting { Key = "GitHub:Token", Value = "enc:v1:garbage", IsSecret = true } };
        var config = SettingsConfigurationMapper.ToConfiguration(rows, throwing);
        Assert.False(config.ContainsKey("GitHub:Token"));
    }

    [Fact]
    public void Join_and_split_round_trip()
    {
        Assert.Equal("a,b", SettingsConfigurationMapper.JoinList([" a ", "", "b"]));
        Assert.Equal(["a", "b"], SettingsConfigurationMapper.SplitList(" a , ,b"));
        Assert.Empty(SettingsConfigurationMapper.SplitList(null));
    }
}

public sealed class ThrowingProtector : ISecretProtector
{
    public string Protect(string plaintext) => throw new InvalidOperationException("no key");
    public string Unprotect(string ciphertext) => throw new InvalidOperationException("no key");
}
```

`SettingsSeederTests.cs`:
```csharp
using Cicd.Core.Settings;
using Microsoft.Extensions.Configuration;

namespace Cicd.Core.Tests;

public class SettingsSeederTests
{
    private static IConfiguration Config(params (string Key, string Value)[] pairs) =>
        new ConfigurationBuilder().AddInMemoryCollection(pairs.Select(p => new KeyValuePair<string, string?>(p.Key, p.Value))).Build();

    [Fact]
    public void Seeds_only_cataloged_keys_that_are_missing()
    {
        var config = Config(("Server:PublicUrl", "http://x"), ("GitHub:Token", "ghp"), ("ConnectionStrings:Cicd", "Host=..."));
        var rows = SettingsSeeder.MissingRows(config, existingKeys: ["Server:PublicUrl"], new ReversingProtector(), new TestClock());
        var keys = rows.Select(r => r.Key).ToList();
        Assert.DoesNotContain("Server:PublicUrl", keys);
        Assert.Contains("GitHub:Token", keys);
        Assert.DoesNotContain("ConnectionStrings:Cicd", keys);
        var token = rows.Single(r => r.Key == "GitHub:Token");
        Assert.True(token.IsSecret);
        Assert.Equal(SecretCodec.Encode(new ReversingProtector(), "ghp"), token.Value);
        Assert.Equal("seed", token.UpdatedBy);
    }

    [Fact]
    public void Missing_configuration_seeds_an_empty_value_and_arrays_are_joined()
    {
        var config = Config(("Security:BootstrapAdmins:0", "a@x.com"), ("Security:BootstrapAdmins:1", "b@x.com"));
        var rows = SettingsSeeder.MissingRows(config, [], new ReversingProtector(), new TestClock());
        Assert.Equal("a@x.com,b@x.com", rows.Single(r => r.Key == "Security:BootstrapAdmins").Value);
        Assert.Equal("", rows.Single(r => r.Key == "GitHub:ApiBaseUrl").Value);
        Assert.Equal(SettingsCatalog.All.Count, rows.Count);
    }
}
```

`SettingsServiceTests.cs`:
```csharp
using Cicd.Core.Entities;
using Cicd.Core.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cicd.Core.Tests;

public sealed class FakeReloader : ISettingsReloader
{
    public Dictionary<string, string?> Startup { get; } = new(StringComparer.OrdinalIgnoreCase);
    public int Reloads { get; private set; }
    public IReadOnlyDictionary<string, string?> ValuesAtStartup => Startup;
    public void Reload() => Reloads++;
}

public class SettingsServiceTests : IDisposable
{
    private readonly TestDb testDb = new(new ReversingProtector());
    private readonly ReversingProtector protector = new();
    private readonly FakeReloader reloader = new();
    private readonly TestClock clock = new();

    private SettingsService Service(Cicd.Core.Persistence.CicdDbContext db) => new(db, protector, reloader, clock, NullLogger<SettingsService>.Instance);

    [Fact]
    public async Task Read_masks_secrets_and_reports_missing_rows_as_unset()
    {
        await using var db = testDb.Create();
        db.Settings.Add(new Setting { Key = "GitHub:Token", Value = SecretCodec.Encode(protector, "ghp"), IsSecret = true });
        db.Settings.Add(new Setting { Key = "Server:PublicUrl", Value = "http://x" });
        await db.SaveChangesAsync();
        var views = await Service(db).GetAllAsync(CancellationToken.None);
        Assert.Equal(SettingsCatalog.All.Count, views.Count);
        Assert.Equal("********", views.Single(v => v.Definition.Key == "GitHub:Token").Value);
        Assert.Equal("http://x", views.Single(v => v.Definition.Key == "Server:PublicUrl").Value);
        var unset = views.Single(v => v.Definition.Key == "Security:ApiToken");
        Assert.False(unset.IsSet);
        Assert.Equal("", unset.Value);
    }

    [Fact]
    public async Task Update_validates_writes_encodes_and_reloads()
    {
        await using var db = testDb.Create();
        var service = Service(db);
        await service.UpdateAsync(new Dictionary<string, string?>
        {
            ["Server:DispatchIntervalSeconds"] = "5",
            ["Agents:AuthToken"] = "s3cret",
            ["Security:BootstrapAdmins"] = "a@x.com, b@x.com",
        }, "alice", CancellationToken.None);
        var token = await db.Settings.SingleAsync(s => s.Key == "Agents:AuthToken");
        Assert.True(token.IsSecret);
        Assert.Equal(SecretCodec.Encode(protector, "s3cret"), token.Value);
        Assert.Equal("alice", token.UpdatedBy);
        Assert.Equal("a@x.com,b@x.com", (await db.Settings.SingleAsync(s => s.Key == "Security:BootstrapAdmins")).Value);
        Assert.Equal(1, reloader.Reloads);
    }

    [Fact]
    public async Task Empty_secret_means_unchanged()
    {
        await using var db = testDb.Create();
        var service = Service(db);
        await service.UpdateAsync(new Dictionary<string, string?> { ["Agents:AuthToken"] = "first" }, null, CancellationToken.None);
        await service.UpdateAsync(new Dictionary<string, string?> { ["Agents:AuthToken"] = "" }, null, CancellationToken.None);
        Assert.Equal(SecretCodec.Encode(protector, "first"), (await db.Settings.SingleAsync(s => s.Key == "Agents:AuthToken")).Value);
    }

    [Fact]
    public void Validation_rejects_bad_values()
    {
        var errors = SettingsService.Validate(new Dictionary<string, string?>
        {
            ["Server:DispatchIntervalSeconds"] = "-1",
            ["Agents:AutoAuthorize"] = "maybe",
            ["Nope:Key"] = "x",
        });
        Assert.Equal(3, errors.Count);
        Assert.Empty(SettingsService.Validate(new Dictionary<string, string?> { ["Agents:AutoAuthorize"] = "TRUE", ["Server:PublicUrl"] = "" }));
    }

    [Fact]
    public async Task Update_with_invalid_values_writes_nothing()
    {
        await using var db = testDb.Create();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service(db).UpdateAsync(
            new Dictionary<string, string?> { ["Server:PublicUrl"] = "http://x", ["Server:DispatchIntervalSeconds"] = "x" }, null, CancellationToken.None));
        Assert.Equal(0, await db.Settings.CountAsync());
        Assert.Equal(0, reloader.Reloads);
    }

    [Fact]
    public async Task Restart_pending_when_stored_differs_from_startup_value()
    {
        reloader.Startup["IdentityProvider:Authority"] = "https://old";
        await using var db = testDb.Create();
        db.Settings.Add(new Setting { Key = "IdentityProvider:Authority", Value = "https://new" });
        db.Settings.Add(new Setting { Key = "Server:PublicUrl", Value = "http://changed" });
        await db.SaveChangesAsync();
        var views = await Service(db).GetAllAsync(CancellationToken.None);
        Assert.True(views.Single(v => v.Definition.Key == "IdentityProvider:Authority").RestartPending);
        Assert.False(views.Single(v => v.Definition.Key == "Server:PublicUrl").RestartPending);
    }

    public void Dispose() => testDb.Dispose();
}
```

- [ ] **Step 2: Run to verify failure** — `dotnet test tests/Cicd.Core.Tests --filter "SettingsConfigurationMapperTests|SettingsSeederTests|SettingsServiceTests"` → build errors.

- [ ] **Step 3: Implement**

`SettingsConfigurationMapper.cs`:
```csharp
using Cicd.Core.Entities;
using Microsoft.Extensions.Logging;

namespace Cicd.Core.Settings;

/// <summary>Turns settings rows into configuration keys: secrets decoded, lists expanded to indexed children.</summary>
public static class SettingsConfigurationMapper
{
    public static IDictionary<string, string?> ToConfiguration(IEnumerable<Setting> rows, ISecretProtector protector, ILogger? logger = null)
    {
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            var definition = SettingsCatalog.Find(row.Key);
            string value;
            try
            {
                value = row.IsSecret ? SecretCodec.Decode(protector, row.Value) : row.Value;
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "Setting {Key} cannot be decrypted; it is ignored until re-entered", row.Key);
                continue;
            }
            if (definition?.Kind == SettingKind.List)
            {
                var items = SplitList(value);
                for (var i = 0; i < items.Count; i++)
                {
                    result[$"{row.Key}:{i}"] = items[i];
                }
                continue;
            }
            result[row.Key] = value;
        }
        return result;
    }

    public static string JoinList(IEnumerable<string> items) => string.Join(',', items.Select(i => i.Trim()).Where(i => i.Length > 0));

    public static IReadOnlyList<string> SplitList(string? value) =>
        string.IsNullOrWhiteSpace(value) ? [] : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
```

`SettingsSeeder.cs`:
```csharp
using Cicd.Core.Entities;
using Microsoft.Extensions.Configuration;

namespace Cicd.Core.Settings;

/// <summary>First-run seeding: every cataloged key without a row gets one from the current configuration (appsettings, environment).</summary>
public static class SettingsSeeder
{
    public const string SeedAuthor = "seed";

    public static IReadOnlyList<Setting> MissingRows(IConfiguration configuration, IEnumerable<string> existingKeys, ISecretProtector protector, TimeProvider clock)
    {
        var existing = new HashSet<string>(existingKeys, StringComparer.OrdinalIgnoreCase);
        var now = clock.GetUtcNow();
        var rows = new List<Setting>();
        foreach (var definition in SettingsCatalog.All)
        {
            if (existing.Contains(definition.Key))
            {
                continue;
            }
            var value = definition.Kind == SettingKind.List
                ? SettingsConfigurationMapper.JoinList(configuration.GetSection(definition.Key).GetChildren().Select(c => c.Value ?? ""))
                : configuration[definition.Key] ?? "";
            var isSecret = definition.Kind == SettingKind.Secret;
            rows.Add(new Setting
            {
                Key = definition.Key,
                Value = isSecret && value.Length > 0 ? SecretCodec.Encode(protector, value) : value,
                IsSecret = isSecret,
                UpdatedAt = now,
                UpdatedBy = SeedAuthor,
            });
        }
        return rows;
    }
}
```
(An empty secret is stored as `""`, not encoded, so "unset" stays distinguishable.)

`SettingsService.cs`:
```csharp
using Cicd.Core.Entities;
using Cicd.Core.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Cicd.Core.Settings;

/// <summary>Lets the settings service push new values into the live configuration and see what the host started with.</summary>
public interface ISettingsReloader
{
    IReadOnlyDictionary<string, string?> ValuesAtStartup { get; }
    void Reload();
}

public sealed record SettingView(SettingDefinition Definition, string Value, bool IsSet, bool RestartPending, bool Unreadable);

public sealed class SettingsService(CicdDbContext db, ISecretProtector protector, ISettingsReloader reloader, TimeProvider clock, ILogger<SettingsService> logger)
{
    public const string Mask = "********";

    public async Task<IReadOnlyList<SettingView>> GetAllAsync(CancellationToken cancellationToken)
    {
        var rows = await db.Settings.AsNoTracking().ToDictionaryAsync(s => s.Key, StringComparer.OrdinalIgnoreCase, cancellationToken);
        var views = new List<SettingView>();
        foreach (var definition in SettingsCatalog.All)
        {
            rows.TryGetValue(definition.Key, out var row);
            var stored = row?.Value ?? "";
            var unreadable = false;
            var value = stored;
            if (definition.Kind == SettingKind.Secret)
            {
                try
                {
                    value = SecretCodec.Decode(protector, stored);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Setting {Key} cannot be decrypted", definition.Key);
                    unreadable = true;
                    value = "";
                }
            }
            var isSet = value.Length > 0;
            var restartPending = definition.RestartRequired
                && !string.Equals(value, reloader.ValuesAtStartup.GetValueOrDefault(definition.Key) ?? "", StringComparison.Ordinal);
            var shown = definition.Kind == SettingKind.Secret ? (isSet ? Mask : "") : value;
            views.Add(new SettingView(definition, shown, isSet, restartPending, unreadable));
        }
        return views;
    }

    /// <summary>Validates everything first; on any error nothing is written. Empty secrets mean "unchanged".</summary>
    public async Task UpdateAsync(IReadOnlyDictionary<string, string?> values, string? updatedBy, CancellationToken cancellationToken)
    {
        var errors = Validate(values);
        if (errors.Count > 0)
        {
            throw new InvalidOperationException(string.Join(" ", errors));
        }
        var now = clock.GetUtcNow();
        foreach (var (key, raw) in values)
        {
            var definition = SettingsCatalog.Find(key)!;
            var value = raw ?? "";
            if (definition.Kind == SettingKind.Secret && value.Length == 0)
            {
                continue;
            }
            var row = await db.Settings.FirstOrDefaultAsync(s => s.Key == definition.Key, cancellationToken);
            if (row is null)
            {
                row = new Setting { Key = definition.Key };
                db.Settings.Add(row);
            }
            row.Value = definition.Kind switch
            {
                SettingKind.Secret => SecretCodec.Encode(protector, value),
                SettingKind.List => SettingsConfigurationMapper.JoinList(SettingsConfigurationMapper.SplitList(value)),
                SettingKind.Boolean => bool.Parse(value).ToString().ToLowerInvariant(),
                _ => value.Trim(),
            };
            row.IsSecret = definition.Kind == SettingKind.Secret;
            row.UpdatedAt = now;
            row.UpdatedBy = updatedBy;
        }
        await db.SaveChangesAsync(cancellationToken);
        reloader.Reload();
    }

    public static IReadOnlyList<string> Validate(IReadOnlyDictionary<string, string?> values)
    {
        var errors = new List<string>();
        foreach (var (key, raw) in values)
        {
            var definition = SettingsCatalog.Find(key);
            if (definition is null)
            {
                errors.Add($"'{key}' is not a managed setting.");
                continue;
            }
            var value = (raw ?? "").Trim();
            switch (definition.Kind)
            {
                case SettingKind.Number when value.Length > 0 && (!int.TryParse(value, out var number) || number < 0):
                    errors.Add($"{definition.DisplayName} must be a non-negative whole number.");
                    break;
                case SettingKind.Boolean when !bool.TryParse(value, out _):
                    errors.Add($"{definition.DisplayName} must be true or false.");
                    break;
            }
        }
        return errors;
    }
}
```
Register in `AddCicdCore`: `services.AddScoped<SettingsService>();` (`using Cicd.Core.Settings;`). Note `ISecretProtector` and `ISettingsReloader` are registered by the Server in Task 3.

- [ ] **Step 4: Run the three test classes** → pass (Core 46 + 4 + 2 + 6 = 58). Commit: `Add settings mapper, seeder and service`.

---

### Task 3: Database configuration provider, Data Protection protector, startup order

**Files:**
- Create: `src/Cicd.Data/DatabaseSettingsConfiguration.cs`, `src/Cicd.Data/DatabaseStartup.cs`, `src/Cicd.Server/Security/DataProtectionSecretProtector.cs`, `tests/Cicd.Server.Tests/DataProtectionSecretProtectorTests.cs`
- Modify: `src/Cicd.Data/Cicd.Data.csproj` (add `<PackageReference Include="Microsoft.Extensions.Configuration" />` and add `<PackageVersion Include="Microsoft.Extensions.Configuration" Version="10.0.12" />` to `Directory.Packages.props`), `src/Cicd.Server/Program.cs`

**Interfaces (produced):**
```csharp
// Cicd.Data
public sealed class DatabaseSettingsConfigurationSource(string connectionString, ISecretProtector protector) : IConfigurationSource { DatabaseSettingsConfigurationProvider Provider { get; } }
public sealed class DatabaseSettingsConfigurationProvider : ConfigurationProvider, ISettingsReloader { void Load(); void Reload(); IReadOnlyDictionary<string,string?> ValuesAtStartup; }
public static class DatabaseStartup { static Task MigrateAsync(string connectionString, CancellationToken); static Task SeedSettingsAsync(string connectionString, IConfiguration configuration, ISecretProtector protector, CancellationToken); }
// Cicd.Server.Security
public sealed class DataProtectionSecretProtector(IDataProtectionProvider provider) : ISecretProtector  // purpose "Cicd.Secrets"
```

- [ ] **Step 1: Failing test** `tests/Cicd.Server.Tests/DataProtectionSecretProtectorTests.cs`:
```csharp
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
```
Run → build error.

- [ ] **Step 2: Implement**

`DataProtectionSecretProtector.cs`:
```csharp
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
```

`src/Cicd.Data/DatabaseSettingsConfiguration.cs`:
```csharp
using Cicd.Core.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Cicd.Data;

/// <summary>Configuration source over the settings table. Added last so stored values win over appsettings and environment.</summary>
public sealed class DatabaseSettingsConfigurationSource(string connectionString, ISecretProtector protector) : IConfigurationSource
{
    public DatabaseSettingsConfigurationProvider Provider { get; } = new(connectionString, protector);

    public IConfigurationProvider Build(IConfigurationBuilder builder) => Provider;
}

public sealed class DatabaseSettingsConfigurationProvider(string connectionString, ISecretProtector protector) : ConfigurationProvider, ISettingsReloader
{
    private Dictionary<string, string?>? startup;

    public IReadOnlyDictionary<string, string?> ValuesAtStartup => startup ?? new Dictionary<string, string?>();

    public override void Load()
    {
        var options = PostgresServiceCollectionExtensions.Configure(new DbContextOptionsBuilder<PostgresCicdDbContext>(), connectionString).Options;
        using var db = new PostgresCicdDbContext(options, protector);
        var rows = db.Settings.AsNoTracking().ToList();
        var mapped = SettingsConfigurationMapper.ToConfiguration(rows, protector);
        Data = new Dictionary<string, string?>(mapped, StringComparer.OrdinalIgnoreCase);
        startup ??= new Dictionary<string, string?>(Data, StringComparer.OrdinalIgnoreCase);
    }

    public void Reload()
    {
        Load();
        OnReload();
    }
}
```
Note `ValuesAtStartup` holds the *expanded* keys; the service compares restart-required keys, which are all scalar (`IdentityProvider:*`) except `Plugins:Disabled` (List). For the List case compare the joined value: in `SettingsService`, when the definition is a List, compute the startup value as `JoinList` of `ValuesAtStartup` entries whose key starts with `definition.Key + ":"`, ordered by index. Implement that in `SettingsService.GetAllAsync` with a small private helper `StartupValue(definition)`.

`src/Cicd.Data/DatabaseStartup.cs`:
```csharp
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

    private static PostgresCicdDbContext Create(string connectionString, ISecretProtector protector) =>
        new(PostgresServiceCollectionExtensions.Configure(new DbContextOptionsBuilder<PostgresCicdDbContext>(), connectionString).Options, protector);
}
```

`Program.cs`: replace everything from `var connectionString = ...` through the `MigrateOnStartup` block after `builder.Build()` with this ordering (keep the rest):
```csharp
var connectionString = builder.Configuration.GetConnectionString("Cicd")
    ?? throw new InvalidOperationException("ConnectionStrings:Cicd is not configured.");
var dataDirectory = Path.GetFullPath(builder.Configuration["Server:DataDirectory"] ?? "data");
var keyDirectory = new DirectoryInfo(Path.Combine(dataDirectory, "keys"));
keyDirectory.Create();
var secretProtector = new DataProtectionSecretProtector(
    DataProtectionProvider.Create(keyDirectory, o => o.SetApplicationName("cicd")));

using (var startupLoggers = LoggerFactory.Create(l => l.AddSimpleConsole()))
{
    var startupLogger = startupLoggers.CreateLogger("Startup");
    if (builder.Configuration.GetValue("Server:MigrateOnStartup", true))
    {
        startupLogger.LogInformation("Applying database migrations");
        await DatabaseStartup.MigrateAsync(connectionString);
    }
    var seeded = await DatabaseStartup.SeedSettingsAsync(connectionString, builder.Configuration, secretProtector);
    if (seeded > 0)
    {
        startupLogger.LogInformation("Seeded {Count} settings from configuration", seeded);
    }
    var settingsSource = new DatabaseSettingsConfigurationSource(connectionString, secretProtector);
    builder.Configuration.Add(settingsSource);
    builder.Services.AddSingleton<ISecretProtector>(secretProtector);
    builder.Services.AddSingleton<ISettingsReloader>(settingsSource.Provider);
    builder.Services.AddDataProtection().PersistKeysToFileSystem(keyDirectory).SetApplicationName("cicd");

    builder.Services.LoadPlugins(builder.Configuration, PluginSide.Server, startupLoggers.CreateLogger("Plugins"));
}
```
Add `using Cicd.Core.Settings; using Microsoft.AspNetCore.DataProtection;`. Delete the old post-build migration block (`serverOptions.MigrateOnStartup` …) and the now-unused `serverOptions` variable. Everything after (`AddCicdPostgres`, `AddCicdCore`, …) is unchanged. `builder.Configuration.Add(...)` triggers an immediate `Load()` of the provider, so `builder.Configuration["Agents:AuthToken"]` further down already reflects the database.

- [ ] **Step 3: Build, unit tests, smoke**

`dotnet build Cicd.slnx && dotnet test Cicd.slnx` (Server 73 + 2). Smoke against the container (Development, both env vars, `Agents__AuthToken=dev`): start, wait for "Now listening", then:
```bash
docker exec cicd-postgres psql -U cicd -d cicd -Atc "select key, is_secret, left(value, 12), updated_by from settings order by key"
docker exec cicd-postgres psql -U cicd -d cicd -Atc "select data_type from information_schema.columns where table_name='vcs_roots' and column_name='properties'"
ls data/keys
```
Expected: all 19 catalog keys present with `updated_by = seed`; `Agents:AuthToken` is `t` with value starting `enc:v1:`; `IdentityProvider:Authority` = `https://identity-dev.manageamerica.com` (from the Development file); `properties` column `text`; one `key-*.xml` in `data/keys`. `curl -s -o /dev/null -w '%{http_code}' localhost:5000/login` = 200. Kill the server. Record the verbatim output in the report. Commit: `Load settings from the database at startup and protect secrets with Data Protection`.

---

### Task 4: Live consumers and the settings API

**Files:**
- Modify: `src/Cicd.Server/Security/TokenAuthentication.cs`, `RoleRequirement.cs`, `V21LoginClient.cs`, `AuthEndpoints.cs`, `src/Cicd.Core/Users/UserService.cs`, `src/Cicd.Core/PullRequests/PullRequestStatusNotifier.cs`, `src/Cicd.Core/Builds/BuildDispatcher.cs`, `src/Cicd.Core/Triggers/TriggerService.cs`, `src/Cicd.Core/PullRequests/PullRequestService.cs`, `src/Cicd.Server/Components/Layout/MainLayout.razor`, `src/Cicd.Server/Components/Pages/Login.razor`, tests that construct these types (`RoleRequirementHandlerTests`, `UserServiceTests`, `LocalUserClaimsTransformationTests`, `UserRevalidatingAuthenticationStateProviderTests`, `V21LoginClientTests`)
- Create: `src/Cicd.Server/Api/SettingsEndpoints.cs`; add DTOs to `src/Cicd.Contracts/ApiModels.cs`
- Modify: `src/Cicd.Server/Api/ApiEndpoints.cs` (`MapCicdApi` calls `MapSettings(api.MapGroup("/settings").WithTags("Settings").RequireAuthorization(Policies.Admin))`)

**Interfaces (produced):**
```csharp
// Cicd.Contracts.Api
public sealed record SettingDto(string Key, string Section, string DisplayName, string Description, string Kind, bool RestartRequired, string Value, bool IsSet, bool RestartPending, bool Unreadable);
public sealed record UpdateSettingsRequest(IReadOnlyDictionary<string, string?> Values);
// GET /api/v1/settings -> SettingDto[]; PUT /api/v1/settings -> 204, or 400 ValidationProblem { settings: [errors] }
```

- [ ] **Step 1: Switch consumers to `IOptionsMonitor<T>`**

Mechanical: change each constructor parameter `IOptions<X> name` to `IOptionsMonitor<X> name` and every `name.Value` to `name.CurrentValue`. In the three hosted services replace the fixed `PeriodicTimer` with a loop that re-reads the interval every iteration:
```csharp
        while (!stoppingToken.IsCancellationRequested)
        {
            var interval = TimeSpan.FromSeconds(Math.Max(1, options.CurrentValue.DispatchIntervalSeconds));
            await Task.Delay(interval, stoppingToken);
            try { ... existing body ... }
            catch (Exception ex) when (ex is not OperationCanceledException) { ... }
        }
```
(keep each service's existing minimum: 1 for dispatch, 5 for triggers, 10 for pull requests; braces on the try/catch). `IOptions<CicdServerOptions>` in `ArtifactStore` stays (`DataDirectory` is not managed). In Razor: `@inject IOptionsMonitor<IdentityProviderOptions> Identity` and `Identity.CurrentValue.IsConfigured`. Tests: where a test builds one of these types with `Options.Create(x)`, wrap it in a tiny helper added to each test project, `tests/.../TestOptions.cs`:
```csharp
using Microsoft.Extensions.Options;

namespace Cicd.Core.Tests; // or Cicd.Server.Tests

/// <summary>A fixed IOptionsMonitor for tests.</summary>
public sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
{
    public T CurrentValue { get; } = value;
    public T Get(string? name) => CurrentValue;
    public IDisposable? OnChange(Action<T, string?> listener) => null;
}
```
and replace `Options.Create(new X { ... })` with `new StaticOptionsMonitor<X>(new X { ... })` at the affected call sites. All existing tests must pass unchanged in behavior.

- [ ] **Step 2: Settings API** `src/Cicd.Server/Api/SettingsEndpoints.cs`:
```csharp
using System.Security.Claims;
using Cicd.Contracts.Api;
using Cicd.Core.Settings;
using Cicd.Server.Security;

namespace Cicd.Server.Api;

public static class SettingsEndpoints
{
    public static void MapSettings(RouteGroupBuilder group)
    {
        group.MapGet("/", async Task<IResult> (SettingsService settings, CancellationToken ct) =>
            Results.Ok((await settings.GetAllAsync(ct)).Select(ToDto).ToList()));

        group.MapPut("/", async Task<IResult> (UpdateSettingsRequest request, SettingsService settings, ClaimsPrincipal caller, CancellationToken ct) =>
        {
            var errors = SettingsService.Validate(request.Values);
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["settings"] = [.. errors] });
            }
            await settings.UpdateAsync(request.Values, caller.Identity?.Name ?? "api-token", ct);
            return Results.NoContent();
        });
    }

    private static SettingDto ToDto(SettingView view) => new(
        view.Definition.Key, view.Definition.Section, view.Definition.DisplayName, view.Definition.Description,
        view.Definition.Kind.ToString(), view.Definition.RestartRequired, view.Value, view.IsSet, view.RestartPending, view.Unreadable);
}
```
Make `MapSettings` `internal static` and call it from `ApiEndpoints.MapCicdApi` (add `MapSettings(api.MapGroup("/settings").WithTags("Settings").RequireAuthorization(Policies.Admin));`). Add the two records to `ApiModels.cs`.

- [ ] **Step 3: Build, tests, smoke**

`dotnet build Cicd.slnx && dotnet test Cicd.slnx`. Smoke (Development, `Agents__AuthToken=dev Security__ApiToken=admin-dev`; note these env values only matter on first seed — the table is already seeded from Task 3's run, so **first reset the two rows** to known values: `docker exec cicd-postgres psql -U cicd -d cicd -c "delete from settings where key in ('Agents:AuthToken','Security:ApiToken')"` so the seeder re-inserts them from the env vars):
```bash
H='Authorization: Bearer admin-dev'
curl -s -H "$H" localhost:5000/api/v1/settings | python3 -c "import sys,json; d=json.load(sys.stdin); print(len(d)); print([ (x['key'],x['value'],x['isSet']) for x in d if x['key'] in ('Agents:AuthToken','Server:PublicUrl','Security:BootstrapAdmins')])"
curl -s -o /dev/null -w 'put %{http_code}\n' -X PUT -H "$H" -H 'content-type: application/json' -d '{"values":{"Server:PublicUrl":"http://ci.local","Agents:AuthToken":"agent-new"}}' localhost:5000/api/v1/settings
curl -s -H "$H" localhost:5000/api/v1/settings | python3 -c "import sys,json; print([x['value'] for x in json.load(sys.stdin) if x['key']=='Server:PublicUrl'])"
curl -s -o /dev/null -w 'old agent token %{http_code}\n' -X POST -H 'Authorization: Bearer dev' 'localhost:5000/api/v1/builds/00000000-0000-0000-0000-000000000001/artifacts?path=x' --data-binary x
curl -s -o /dev/null -w 'new agent token %{http_code}\n' -X POST -H 'Authorization: Bearer agent-new' 'localhost:5000/api/v1/builds/00000000-0000-0000-0000-000000000001/artifacts?path=x' --data-binary x
curl -s -o /dev/null -w 'bad put %{http_code}\n' -X PUT -H "$H" -H 'content-type: application/json' -d '{"values":{"Server:DispatchIntervalSeconds":"x"}}' localhost:5000/api/v1/settings
curl -s -o /dev/null -w 'anon %{http_code}\n' localhost:5000/api/v1/settings
```
Expected: `19`; `Agents:AuthToken` value `********` isSet true; `put 204`; `['http://ci.local']`; `old agent token 401`; `new agent token 404` (authenticated as agent, build not found) — this proves the token changed **without restart**; `bad put 400`; `anon 401`. Kill the server. Commit: `Read settings live and add the settings API`.

---

### Task 5: Admin area, settings page, docs

**Files:**
- Create: `src/Cicd.Server/Components/Pages/Admin/Settings.razor`
- Modify: `src/Cicd.Server/Components/Layout/MainLayout.razor`, `src/Cicd.Server/wwwroot/app.css`, `README.md`, `docs/ARCHITECTURE.md`, `HANDOFF.md`

- [ ] **Step 1: Sidebar**

In `MainLayout.razor` replace the `Agents`, `Plugins` and the Admin-gated `Users` links with:
```razor
        <NavLink href="/" Match="NavLinkMatch.All">Overview</NavLink>
        <NavLink href="/projects">Projects</NavLink>
        <NavLink href="/builds">Builds</NavLink>
        <AuthorizeView Policy="@Policies.Admin">
            <div class="nav-group">Admin</div>
            <NavLink href="/admin/settings">Settings</NavLink>
            <NavLink href="/users">Users</NavLink>
            <NavLink href="/agents">Agents</NavLink>
            <NavLink href="/plugins">Plugins</NavLink>
        </AuthorizeView>
        <a href="/api/docs" target="_blank" rel="noopener">API docs</a>
```
CSS: `.nav-group { padding: 14px 20px 4px; color: var(--muted); font-size: 11px; text-transform: uppercase; letter-spacing: .5px; }` plus `.settings-section { background: var(--panel); border: 1px solid var(--line); padding: 16px 24px; margin-bottom: 16px; } .settings-section h2 { margin-top: 0; } .setting { display: grid; grid-template-columns: 220px 1fr; gap: 6px 16px; padding: 8px 0; border-bottom: 1px solid var(--line); } .setting .hint { grid-column: 2; color: var(--muted); font-size: 12px; } .setting input[type=text], .setting input[type=password], .setting input[type=number] { width: 100%; max-width: 520px; } .banner { border: 1px solid var(--warn); padding: 12px; margin-bottom: 16px; }`.

- [ ] **Step 2: Page** `Components/Pages/Admin/Settings.razor`:
```razor
@page "/admin/settings"
@attribute [Authorize(Policy = Policies.Admin)]
@inherits SecurePage
@inject IServiceScopeFactory Scopes

<PageTitle>Settings - CICD</PageTitle>
<h1>Settings</h1>
<p class="muted">Stored in the database and applied immediately, except where a restart is noted.</p>

@if (restartPending.Count > 0)
{
    <div class="banner">Restart the server to apply: @string.Join(", ", restartPending)</div>
}
@if (error is not null)
{
    <div class="error">@error</div>
}
@if (saved is not null)
{
    <p class="muted">Saved @saved.</p>
}

@foreach (var section in views.GroupBy(v => v.Definition.Section))
{
    <div class="settings-section">
        <h2>@section.Key</h2>
        @foreach (var view in section)
        {
            var key = view.Definition.Key;
            <div class="setting">
                <label for="@key">@view.Definition.DisplayName @(view.Definition.RestartRequired ? "(restart)" : "")</label>
                @switch (view.Definition.Kind)
                {
                    case SettingKind.Boolean:
                        <input id="@key" type="checkbox" checked="@(edits[key] == "true")" @onchange="e => edits[key] = (bool)e.Value! ? "true" : "false"" />
                        break;
                    case SettingKind.Number:
                        <input id="@key" type="number" min="0" value="@edits[key]" @onchange="e => edits[key] = e.Value?.ToString() ?? string.Empty" />
                        break;
                    case SettingKind.Secret:
                        <input id="@key" type="password" placeholder="@(view.Unreadable ? "unreadable, re-enter" : view.IsSet ? "set, enter a new value to replace" : "not set")" autocomplete="new-password" value="@edits[key]" @onchange="e => edits[key] = e.Value?.ToString() ?? string.Empty" />
                        break;
                    default:
                        <input id="@key" type="text" value="@edits[key]" @onchange="e => edits[key] = e.Value?.ToString() ?? string.Empty" />
                        break;
                }
                <span class="hint">@view.Definition.Description @(view.Definition.Kind == SettingKind.List ? "Comma-separated." : "")</span>
            </div>
        }
        <button @onclick="() => SaveAsync(section.Key)">Save @section.Key</button>
    </div>
}

@code {
    private IReadOnlyList<SettingView> views = [];
    private readonly Dictionary<string, string> edits = new(StringComparer.OrdinalIgnoreCase);
    private List<string> restartPending = [];
    private string? error;
    private string? saved;

    protected override Task OnInitializedAsync() => LoadAsync();

    private async Task LoadAsync()
    {
        await using var scope = Scopes.CreateAsyncScope();
        views = await scope.ServiceProvider.GetRequiredService<SettingsService>().GetAllAsync(CancellationToken.None);
        edits.Clear();
        foreach (var view in views)
        {
            edits[view.Definition.Key] = view.Definition.Kind == SettingKind.Secret ? "" : view.Value;
        }
        restartPending = views.Where(v => v.RestartPending).Select(v => v.Definition.DisplayName).ToList();
    }

    private async Task SaveAsync(string section)
    {
        error = null;
        saved = null;
        if (!await AllowedAsync(Policies.Admin))
        {
            error = "Admin role required.";
            return;
        }
        var values = views.Where(v => v.Definition.Section == section)
            .ToDictionary(v => v.Definition.Key, v => (string?)edits[v.Definition.Key], StringComparer.OrdinalIgnoreCase);
        var name = (await AuthState).User.Identity?.Name;
        await using var scope = Scopes.CreateAsyncScope();
        try
        {
            await scope.ServiceProvider.GetRequiredService<SettingsService>().UpdateAsync(values, name, CancellationToken.None);
            saved = section;
        }
        catch (InvalidOperationException ex)
        {
            error = ex.Message;
        }
        await LoadAsync();
    }
}
```
Add `@using Cicd.Core.Settings` to `_Imports.razor`. Braces already present; keep them.

- [ ] **Step 3: Docs**

README "Configuration": replace the section with: what stays in `appsettings`/environment (`ConnectionStrings:Cicd`, `Server:DataDirectory`, `Server:MigrateOnStartup`, `Plugins:Directory`, `Logging`, listen URLs); everything else is in the `settings` table, seeded from `appsettings` on the first start and edited at Admin → Settings or `GET/PUT /api/v1/settings`; after seeding, `appsettings` and environment values for managed keys are ignored; secrets encrypted with Data Protection, key ring at `<Server:DataDirectory>/keys` (back it up with the data directory; losing it loses stored secrets and VCS credentials, which must be re-entered); which keys need a restart. Keep the table of keys but retitle it "Managed settings" and add a "Restart" column. In "Users and roles" and "From source" replace instructions that set `IdentityProvider__Authority=` or `Security__ApiToken` via environment with "set it on Admin → Settings (or via the API); environment values only seed the very first start". `docs/ARCHITECTURE.md`: add "Settings and secrets" describing the provider, seeding, reload, restart-required, and the VCS root converter. `HANDOFF.md`: gap 3 → done row in the status table ("Settings in database, secrets at rest | done | seeded 19 keys, PUT changed the agent token live, vcs_roots.properties is encrypted text"); note that `appsettings.Development.json` only matters on first seed; session note.

- [ ] **Step 4: Build, tests, smoke, commit**

Build and full tests. Smoke (Development): `curl -s localhost:5000/` contains `href="/admin/settings"` only for admins — with the static token (`-H 'Authorization: Bearer admin-dev'`) the page body contains `Admin` and `/admin/settings`; `curl -s -H "$H" localhost:5000/admin/settings` = 200 and contains `settings-section` and `Save Server`. Kill the server. Commit: `Add Admin area with settings page; document database settings`.

Final: `dotnet test Cicd.slnx` — Core 58, Server 75.
