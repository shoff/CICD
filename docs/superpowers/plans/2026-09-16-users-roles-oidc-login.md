# Users, Roles and OIDC Login Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the single static admin token with OIDC login against IdentityServer, a local `users` table, and three global roles enforced on the API, the hubs and the Blazor UI.

**Architecture:** One policy scheme forwards each request to the cookie handler (browser), the JwtBearer handler (IdP access tokens) or the existing static token handler (agents, break-glass admin). A claims transformation maps any IdP identity to a local `User` row and adds role claims per request. Authorization is three ordered policies (`Viewer` < `Developer` < `Admin`) backed by one `RoleRequirementHandler`, which also implements "open mode" when OIDC is not configured.

**Tech Stack:** ASP.NET Core 10 minimal APIs and Blazor interactive server, `Microsoft.AspNetCore.Authentication.OpenIdConnect` and `.JwtBearer` 10.0.12, EF Core 10 with Npgsql, xunit with SQLite in-memory for tests.

**Spec:** `docs/superpowers/specs/2026-09-16-users-roles-oidc-login-design.md`

## Global Constraints

- .NET 10 (`net10.0`), SDK pinned by `global.json` (`10.0.100`, `latestFeature`). Package versions live only in `Directory.Packages.props` (central package management); new Microsoft packages use `10.0.12`.
- No leading underscores on C# fields. Use primary constructors. Nullable enabled.
- **Braces are required on every control statement** (`if`, `else`, `for`, `foreach`, `while`, `do`, `using`), even for a single-line body. Where a code block in this plan omits them, add them.
- Never run `dotnet format` or any repo-wide formatter; touch only the files the task names.
- Database naming is snake_case through `UseSnakeCaseNamingConvention()`; `CicdDbContext` in Core stays provider-neutral, `PostgresCicdDbContext` in Data owns migrations.
- The server must keep working with no `Oidc` configuration (open mode) so `dotnet run` without IdP credentials is unchanged.
- Migration command (from README): `cd src/Cicd.Data && DOTNET_ROLL_FORWARD=LatestMajor dotnet ef migrations add <Name> --context PostgresCicdDbContext --output-dir Migrations`.
- Run everything from `/Users/stevenhoff/dev/CICD`. Tests: `dotnet test Cicd.slnx`. Local PostgreSQL is the `cicd-postgres` Docker container (user/password/database all `cicd`).
- Commit after every task. Commit message trailer: `Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>`.

## File map

| File | Responsibility |
| --- | --- |
| `src/Cicd.Contracts/UserRole.cs` | `UserRole` enum shared by entity, DTOs and policies |
| `src/Cicd.Contracts/ApiModels.cs` | `UserDto`, `SetUserRoleRequest`, `SetUserDisabledRequest` (append) |
| `src/Cicd.Core/Entities/User.cs` | `User` entity |
| `src/Cicd.Core/Persistence/CicdDbContext.cs` | `Users` DbSet and mapping (edit) |
| `src/Cicd.Core/Users/SecurityOptions.cs` | `Security` section: `ApiToken`, `BootstrapAdmins` (moved from Server) |
| `src/Cicd.Core/Users/UserService.cs` | upsert on sign-in, list, role and disabled changes with guards |
| `src/Cicd.Core/Services/Mapping.cs` | `User.ToDto()` (append) |
| `src/Cicd.Core/Services/CoreServiceCollectionExtensions.cs` | register `SecurityOptions`, `UserService` (edit) |
| `src/Cicd.Data/Migrations/*_AddUsers.cs` | generated migration |
| `src/Cicd.Server/Security/OidcOptions.cs` | `Oidc` section and `IsConfigured` |
| `src/Cicd.Server/Security/TokenAuthentication.cs` | static token handler, `Roles`, `Policies`, `AgentsOptions` (edit: drop `SecurityOptions`, `AdminRequirement`, DI extension) |
| `src/Cicd.Server/Security/RoleRequirement.cs` | `RoleRequirement`, `RoleRequirementHandler` |
| `src/Cicd.Server/Security/SchemeSelector.cs` | pure request-to-scheme decision |
| `src/Cicd.Server/Security/LocalUserClaimsTransformation.cs` | IdP identity to local user and role claims |
| `src/Cicd.Server/Security/SecurityServiceCollectionExtensions.cs` | `AddCicdSecurity`: all schemes, OIDC events, policies |
| `src/Cicd.Server/Security/AuthEndpoints.cs` | `/login`, `/logout` |
| `src/Cicd.Server/Api/ApiEndpoints.cs` | per-endpoint policies, `MapUsers` (edit) |
| `src/Cicd.Server/Program.cs` | cascading auth state, warnings, auth endpoints, page fallback policy (edit) |
| `src/Cicd.Server/Components/SecurePage.cs` | base class: server-side policy check for page actions |
| `src/Cicd.Server/Components/NotAuthorizedView.razor` | redirect to login or show denied |
| `src/Cicd.Server/Components/Routes.razor`, `_Imports.razor`, `Layout/MainLayout.razor` | auth-aware routing and sidebar user block (edit) |
| `src/Cicd.Server/Components/Pages/Users.razor`, `AccessDenied.razor` | new pages |
| `src/Cicd.Server/Components/Pages/{Projects,ProjectDetail,BuildDetail,Agents,Error}.razor` | role-gated actions, anonymous error page (edit) |
| `src/Cicd.Server/appsettings.json`, `wwwroot/app.css` | config section and styles (edit) |
| `tests/Cicd.Core.Tests/{TestDb,TestClock,UserServiceTests}.cs` | Core tests on SQLite |
| `tests/Cicd.Server.Tests/*` | new test project: selector, requirement handler, claims transformation |
| `README.md`, `docs/ARCHITECTURE.md`, `HANDOFF.md` | docs (edit) |

---

### Task 1: `UserRole`, `User` entity, DbContext mapping, migration

**Files:**
- Create: `src/Cicd.Contracts/UserRole.cs`
- Create: `src/Cicd.Core/Entities/User.cs`
- Modify: `src/Cicd.Core/Persistence/CicdDbContext.cs`
- Modify: `tests/Cicd.Core.Tests/Cicd.Core.Tests.csproj`, `Directory.Packages.props`
- Create: `tests/Cicd.Core.Tests/TestDb.cs`, `tests/Cicd.Core.Tests/UsersTableTests.cs`
- Create (generated): `src/Cicd.Data/Migrations/<timestamp>_AddUsers.cs` and `.Designer.cs`; snapshot updated

**Interfaces:**
- Produces: `enum Cicd.Contracts.UserRole { Viewer = 0, Developer = 1, Admin = 2 }`; `Cicd.Core.Entities.User` with `Id, Issuer, Subject, Username, Email, DisplayName, Role, Disabled, CreatedAt, LastSeenAt`; `CicdDbContext.Users`; test helper `TestDb` with `CicdDbContext Create()`.

- [ ] **Step 1: Add the SQLite provider to central packages and the Core test project**

In `Directory.Packages.props`, inside the `<ItemGroup>`, add:

```xml
    <PackageVersion Include="Microsoft.EntityFrameworkCore.Sqlite" Version="10.0.12" />
    <PackageVersion Include="Microsoft.AspNetCore.Authentication.OpenIdConnect" Version="10.0.12" />
    <PackageVersion Include="Microsoft.AspNetCore.Authentication.JwtBearer" Version="10.0.12" />
```

In `tests/Cicd.Core.Tests/Cicd.Core.Tests.csproj`, add to the package `<ItemGroup>`:

```xml
    <PackageReference Include="Microsoft.EntityFrameworkCore.Sqlite" />
```

- [ ] **Step 2: Write the test helper and the failing test**

`tests/Cicd.Core.Tests/TestDb.cs`:

```csharp
using Cicd.Core.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Cicd.Core.Tests;

/// <summary>In-memory SQLite database with the provider-neutral model. Disposing drops it.</summary>
public sealed class TestDb : IDisposable
{
    private readonly SqliteConnection connection = new("DataSource=:memory:");
    private readonly DbContextOptions<CicdDbContext> options;

    public TestDb()
    {
        connection.Open();
        options = new DbContextOptionsBuilder<CicdDbContext>().UseSqlite(connection).Options;
        using var db = Create();
        db.Database.EnsureCreated();
    }

    public CicdDbContext Create() => new(options);

    public void Dispose() => connection.Dispose();
}
```

`tests/Cicd.Core.Tests/UsersTableTests.cs`:

```csharp
using Cicd.Contracts;
using Cicd.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Cicd.Core.Tests;

public class UsersTableTests
{
    [Fact]
    public async Task Issuer_and_subject_are_unique_and_role_round_trips()
    {
        using var testDb = new TestDb();
        await using (var db = testDb.Create())
        {
            db.Users.Add(new User { Issuer = "https://idp", Subject = "s1", Role = UserRole.Developer });
            await db.SaveChangesAsync();
        }
        await using (var db = testDb.Create())
        {
            var user = await db.Users.SingleAsync();
            Assert.Equal(UserRole.Developer, user.Role);
            Assert.False(user.Disabled);
            db.Users.Add(new User { Issuer = "https://idp", Subject = "s1" });
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }
    }
}
```

- [ ] **Step 3: Run the test to verify it fails**

Run: `dotnet test tests/Cicd.Core.Tests --filter UsersTableTests`
Expected: build error, `'CicdDbContext' does not contain a definition for 'Users'`.

- [ ] **Step 4: Add the enum, the entity and the mapping**

`src/Cicd.Contracts/UserRole.cs`:

```csharp
namespace Cicd.Contracts;

/// <summary>Global roles, ordered from least to most privileged. Comparisons rely on this order.</summary>
public enum UserRole
{
    Viewer = 0,
    Developer = 1,
    Admin = 2,
}
```

`src/Cicd.Core/Entities/User.cs`:

```csharp
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
```

In `src/Cicd.Core/Persistence/CicdDbContext.cs` add after the `TriggerState` DbSet:

```csharp
    public DbSet<User> Users => Set<User>();
```

and after the `TriggerStateEntry` block inside `OnModelCreating`:

```csharp
        modelBuilder.Entity<User>(entity =>
        {
            entity.ToTable("users");
            entity.HasIndex(u => new { u.Issuer, u.Subject }).IsUnique();
            entity.HasIndex(u => u.Email);
            entity.Property(u => u.Role).HasConversion<string>().HasMaxLength(16);
        });
```

- [ ] **Step 5: Run the test to verify it passes**

Run: `dotnet test tests/Cicd.Core.Tests --filter UsersTableTests`
Expected: `Passed! - Failed: 0, Passed: 1`.

- [ ] **Step 6: Generate the migration and check it**

Run:

```bash
cd src/Cicd.Data && DOTNET_ROLL_FORWARD=LatestMajor dotnet ef migrations add AddUsers --context PostgresCicdDbContext --output-dir Migrations && cd ../..
grep -n "users\|ix_users" src/Cicd.Data/Migrations/*_AddUsers.cs
```

Expected: a `CreateTable` for `users` with columns `id, issuer, subject, username, email, display_name, role (character varying(16)), disabled, created_at, last_seen_at`, a unique index `ix_users_issuer_subject` and an index `ix_users_email`. If `dotnet ef` is missing: `dotnet tool install -g dotnet-ef`.

- [ ] **Step 7: Build, run all tests, commit**

Run: `dotnet build Cicd.slnx && dotnet test Cicd.slnx`
Expected: 0 errors, all tests pass (28 existing + 1).

```bash
git add -A && git commit -m "Add User entity, UserRole and the users table migration

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 2: `SecurityOptions` moves to Core; `UserService.EnsureUserAsync`

**Files:**
- Create: `src/Cicd.Core/Users/SecurityOptions.cs`, `src/Cicd.Core/Users/UserService.cs`
- Modify: `src/Cicd.Server/Security/TokenAuthentication.cs` (remove `SecurityOptions` class, add `using Cicd.Core.Users;`, remove `services.Configure<SecurityOptions>` line)
- Modify: `src/Cicd.Core/Services/CoreServiceCollectionExtensions.cs`
- Create: `tests/Cicd.Core.Tests/TestClock.cs`, `tests/Cicd.Core.Tests/UserServiceTests.cs`

**Interfaces:**
- Produces: `record ExternalIdentity(string Issuer, string Subject, string? Username, string? Email, string? DisplayName)`; `UserService(CicdDbContext db, IOptions<SecurityOptions> security, TimeProvider clock)` with `Task<User> EnsureUserAsync(ExternalIdentity, CancellationToken)`; `SecurityOptions { string ApiToken; List<string> BootstrapAdmins }` in `Cicd.Core.Users`; test helper `TestClock : TimeProvider { DateTimeOffset Now }`.

- [ ] **Step 1: Move `SecurityOptions` to Core**

Create `src/Cicd.Core/Users/SecurityOptions.cs`:

```csharp
namespace Cicd.Core.Users;

public sealed class SecurityOptions
{
    public const string SectionName = "Security";
    /// <summary>Static bearer token that grants the admin role. For automation and break-glass use. Empty disables it.</summary>
    public string ApiToken { get; set; } = "";
    /// <summary>Emails promoted to admin every time they sign in. Never demotes anyone.</summary>
    public List<string> BootstrapAdmins { get; set; } = [];
}
```

In `src/Cicd.Server/Security/TokenAuthentication.cs`: delete the `SecurityOptions` class (lines 10-15), add `using Cicd.Core.Users;` at the top, and delete the line `services.Configure<SecurityOptions>(configuration.GetSection(SecurityOptions.SectionName));` from `AddCicdSecurity`.

In `src/Cicd.Core/Services/CoreServiceCollectionExtensions.cs` add `using Cicd.Core.Users;` and, right after the `CicdServerOptions` configure line:

```csharp
        services.Configure<SecurityOptions>(configuration.GetSection(SecurityOptions.SectionName));
        services.AddScoped<UserService>();
```

Run: `dotnet build Cicd.slnx` — expected error only that `UserService` does not exist yet (next steps add it).

- [ ] **Step 2: Write the failing tests**

`tests/Cicd.Core.Tests/TestClock.cs`:

```csharp
namespace Cicd.Core.Tests;

public sealed class TestClock : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 9, 16, 12, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => Now;
}
```

`tests/Cicd.Core.Tests/UserServiceTests.cs`:

```csharp
using Cicd.Contracts;
using Cicd.Core.Persistence;
using Cicd.Core.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Cicd.Core.Tests;

public class UserServiceTests : IDisposable
{
    private static readonly ExternalIdentity Alice = new("https://idp", "sub-alice", "alice", "alice@example.com", "Alice");
    private readonly TestDb testDb = new();
    private readonly TestClock clock = new();

    private UserService Service(CicdDbContext db, params string[] bootstrapAdmins) =>
        new(db, Options.Create(new SecurityOptions { BootstrapAdmins = [.. bootstrapAdmins] }), clock);

    [Fact]
    public async Task New_user_is_created_as_viewer_with_profile_and_last_seen()
    {
        await using var db = testDb.Create();
        var user = await Service(db).EnsureUserAsync(Alice, CancellationToken.None);
        Assert.Equal(UserRole.Viewer, user.Role);
        Assert.Equal("alice", user.Username);
        Assert.Equal("alice@example.com", user.Email);
        Assert.Equal("Alice", user.DisplayName);
        Assert.Equal(clock.Now, user.CreatedAt);
        Assert.Equal(clock.Now, user.LastSeenAt);
        Assert.Equal(1, await db.Users.CountAsync());
    }

    [Fact]
    public async Task Second_sign_in_reuses_the_row_and_refreshes_changed_fields()
    {
        await using (var db = testDb.Create())
        {
            await Service(db).EnsureUserAsync(Alice, CancellationToken.None);
        }
        await using (var db = testDb.Create())
        {
            var user = await Service(db).EnsureUserAsync(Alice with { DisplayName = "Alice Smith", Email = null }, CancellationToken.None);
            Assert.Equal("Alice Smith", user.DisplayName);
            Assert.Equal("alice@example.com", user.Email); // null from the IdP does not erase a known value
            Assert.Equal(1, await db.Users.CountAsync());
        }
    }

    [Fact]
    public async Task Bootstrap_admin_is_promoted_and_never_demoted()
    {
        await using (var db = testDb.Create())
        {
            var user = await Service(db, "ALICE@example.com").EnsureUserAsync(Alice, CancellationToken.None);
            Assert.Equal(UserRole.Admin, user.Role);
        }
        await using (var db = testDb.Create())
        {
            var user = await Service(db).EnsureUserAsync(Alice, CancellationToken.None); // no longer listed
            Assert.Equal(UserRole.Admin, user.Role);
        }
    }

    [Fact]
    public async Task Last_seen_is_written_at_most_every_five_minutes()
    {
        await using var db = testDb.Create();
        var service = Service(db);
        var first = clock.Now;
        await service.EnsureUserAsync(Alice, CancellationToken.None);
        clock.Now = first.AddMinutes(2);
        var user = await service.EnsureUserAsync(Alice, CancellationToken.None);
        Assert.Equal(first, user.LastSeenAt);
        clock.Now = first.AddMinutes(6);
        user = await service.EnsureUserAsync(Alice, CancellationToken.None);
        Assert.Equal(first.AddMinutes(6), user.LastSeenAt);
    }

    public void Dispose() => testDb.Dispose();
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test tests/Cicd.Core.Tests --filter UserServiceTests`
Expected: build error, `UserService` and `ExternalIdentity` not found.

- [ ] **Step 4: Implement `UserService.EnsureUserAsync`**

`src/Cicd.Core/Users/UserService.cs`:

```csharp
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
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/Cicd.Core.Tests --filter UserServiceTests`
Expected: `Passed! - Failed: 0, Passed: 4`.

- [ ] **Step 6: Build everything, run all tests, commit**

Run: `dotnet build Cicd.slnx && dotnet test Cicd.slnx`
Expected: 0 errors, 33 tests pass.

```bash
git add -A && git commit -m "Add UserService sign-in upsert and move SecurityOptions to Core

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 3: User admin operations, DTOs and mapping

**Files:**
- Modify: `src/Cicd.Core/Users/UserService.cs`
- Modify: `src/Cicd.Contracts/ApiModels.cs` (append), `src/Cicd.Core/Services/Mapping.cs` (append)
- Modify: `tests/Cicd.Core.Tests/UserServiceTests.cs` (append tests)

**Interfaces:**
- Consumes: `UserService`, `TestDb`, `TestClock` from Tasks 1-2.
- Produces: `Task<List<User>> ListAsync(CancellationToken)`, `Task<User?> FindAsync(Guid, CancellationToken)`, `Task<User?> SetRoleAsync(Guid id, UserRole role, Guid? actingUserId, CancellationToken)`, `Task<User?> SetDisabledAsync(Guid id, bool disabled, Guid? actingUserId, CancellationToken)`. Null means not found; `InvalidOperationException` means a guard refused. `UserDto`, `SetUserRoleRequest(UserRole Role)`, `SetUserDisabledRequest(bool Disabled)`, `User.ToDto()`.

- [ ] **Step 1: Write the failing tests**

Append inside `UserServiceTests` (before `Dispose`):

```csharp
    private static readonly ExternalIdentity Bob = new("https://idp", "sub-bob", "bob", "bob@example.com", "Bob");

    [Fact]
    public async Task Admin_can_change_another_users_role_and_disable_them()
    {
        await using var db = testDb.Create();
        var service = Service(db, "alice@example.com");
        var alice = await service.EnsureUserAsync(Alice, CancellationToken.None);
        var bob = await service.EnsureUserAsync(Bob, CancellationToken.None);

        var updated = await service.SetRoleAsync(bob.Id, UserRole.Developer, alice.Id, CancellationToken.None);
        Assert.Equal(UserRole.Developer, updated!.Role);
        updated = await service.SetDisabledAsync(bob.Id, true, alice.Id, CancellationToken.None);
        Assert.True(updated!.Disabled);
        Assert.Null(await service.SetRoleAsync(Guid.NewGuid(), UserRole.Admin, alice.Id, CancellationToken.None));
    }

    [Fact]
    public async Task Users_cannot_change_or_disable_themselves()
    {
        await using var db = testDb.Create();
        var service = Service(db, "alice@example.com");
        var alice = await service.EnsureUserAsync(Alice, CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SetRoleAsync(alice.Id, UserRole.Viewer, alice.Id, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SetDisabledAsync(alice.Id, true, alice.Id, CancellationToken.None));
    }

    [Fact]
    public async Task The_last_active_admin_cannot_be_demoted_or_disabled()
    {
        await using var db = testDb.Create();
        var service = Service(db, "alice@example.com");
        var alice = await service.EnsureUserAsync(Alice, CancellationToken.None);
        await service.EnsureUserAsync(Bob, CancellationToken.None);
        // Acting as the static API token (no local user id).
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SetRoleAsync(alice.Id, UserRole.Developer, null, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SetDisabledAsync(alice.Id, true, null, CancellationToken.None));
        Assert.Equal(UserRole.Admin, (await service.FindAsync(alice.Id, CancellationToken.None))!.Role);
    }

    [Fact]
    public async Task Demoting_an_admin_is_allowed_when_another_active_admin_exists()
    {
        await using var db = testDb.Create();
        var service = Service(db, "alice@example.com", "bob@example.com");
        var alice = await service.EnsureUserAsync(Alice, CancellationToken.None);
        await service.EnsureUserAsync(Bob, CancellationToken.None);
        var updated = await service.SetRoleAsync(alice.Id, UserRole.Viewer, null, CancellationToken.None);
        Assert.Equal(UserRole.Viewer, updated!.Role);
        Assert.Equal(2, (await service.ListAsync(CancellationToken.None)).Count);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Cicd.Core.Tests --filter UserServiceTests`
Expected: build error, `SetRoleAsync` not found.

- [ ] **Step 3: Implement the operations**

Append inside `UserService` (after `EnsureUserAsync`, before `IsBootstrapAdmin`):

```csharp
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
```

Append to `src/Cicd.Contracts/ApiModels.cs`:

```csharp
public sealed record UserDto(
    Guid Id,
    string? Username,
    string? Email,
    string? DisplayName,
    UserRole Role,
    bool Disabled,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastSeenAt);

public sealed record SetUserRoleRequest(UserRole Role);

public sealed record SetUserDisabledRequest(bool Disabled);
```

Append inside `Mapping` in `src/Cicd.Core/Services/Mapping.cs` (after `PullRequestDto ToDto`):

```csharp
    public static UserDto ToDto(this User u) => new(u.Id, u.Username, u.Email, u.DisplayName, u.Role, u.Disabled, u.CreatedAt, u.LastSeenAt);
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/Cicd.Core.Tests --filter UserServiceTests`
Expected: `Passed! - Failed: 0, Passed: 8`.

- [ ] **Step 5: Commit**

```bash
dotnet build Cicd.slnx && git add -A && git commit -m "Add user listing, role and disable operations with admin guards

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 4: `OidcOptions`, role ladder, `RoleRequirement`, server test project

**Files:**
- Create: `src/Cicd.Server/Security/OidcOptions.cs`, `src/Cicd.Server/Security/RoleRequirement.cs`
- Modify: `src/Cicd.Server/Security/TokenAuthentication.cs` (replace `Roles`, `Policies`; delete `AdminRequirement` and `AdminRequirementHandler`; update `AddCicdSecurity` policies)
- Create: `tests/Cicd.Server.Tests/Cicd.Server.Tests.csproj`, `tests/Cicd.Server.Tests/RoleRequirementHandlerTests.cs`
- Modify: `Cicd.slnx`

**Interfaces:**
- Produces: `OidcOptions { Authority, ClientId, ClientSecret, Scopes, ValidateAudience, Audience, RequireHttpsMetadata, bool IsConfigured }` with `SectionName = "Oidc"`; `Roles.{Agent,Viewer,Developer,Admin}` strings and `Roles.Of(UserRole)`; `Policies.{Agent,Viewer,Developer,Admin}`; `RoleRequirement(UserRole minimum)`; `RoleRequirementHandler(IOptions<OidcOptions>, IOptions<SecurityOptions>)` with `bool OpenMode` and `static UserRole? Level(ClaimsPrincipal)`.

- [ ] **Step 1: Create the server test project and add it to the solution**

`tests/Cicd.Server.Tests/Cicd.Server.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <IsPackable>false</IsPackable>
    <IsTestProject>true</IsTestProject>
    <NoWarn>$(NoWarn);xUnit1004</NoWarn>
  </PropertyGroup>
  <ItemGroup>
    <Using Include="Xunit" />
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
  </ItemGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" />
    <PackageReference Include="xunit" />
    <PackageReference Include="xunit.runner.visualstudio" />
    <PackageReference Include="Microsoft.EntityFrameworkCore.Sqlite" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="../../src/Cicd.Server/Cicd.Server.csproj" />
  </ItemGroup>
</Project>
```

In `Cicd.slnx`, inside `<Folder Name="/tests/">`, add:

```xml
    <Project Path="tests/Cicd.Server.Tests/Cicd.Server.Tests.csproj" />
```

- [ ] **Step 2: Write the failing tests**

`tests/Cicd.Server.Tests/RoleRequirementHandlerTests.cs`:

```csharp
using System.Security.Claims;
using Cicd.Contracts;
using Cicd.Core.Users;
using Cicd.Server.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;

namespace Cicd.Server.Tests;

public class RoleRequirementHandlerTests
{
    private static RoleRequirementHandler Handler(bool oidcConfigured, string apiToken = "") =>
        new(Options.Create(new OidcOptions { Authority = oidcConfigured ? "https://idp" : "", ClientId = oidcConfigured ? "cicd" : "" }),
            Options.Create(new SecurityOptions { ApiToken = apiToken }));

    private static ClaimsPrincipal UserWith(params string[] roles) =>
        new(new ClaimsIdentity(roles.Select(r => new Claim(ClaimTypes.Role, r)), "test", ClaimTypes.Name, ClaimTypes.Role));

    private static readonly ClaimsPrincipal Anonymous = new(new ClaimsIdentity());

    private static async Task<bool> Passes(RoleRequirementHandler handler, UserRole minimum, ClaimsPrincipal user)
    {
        var context = new AuthorizationHandlerContext([new RoleRequirement(minimum)], user, null);
        await handler.HandleAsync(context);
        return context.HasSucceeded;
    }

    [Theory]
    [InlineData("viewer", UserRole.Viewer, true)]
    [InlineData("viewer", UserRole.Developer, false)]
    [InlineData("viewer", UserRole.Admin, false)]
    [InlineData("developer", UserRole.Viewer, true)]
    [InlineData("developer", UserRole.Developer, true)]
    [InlineData("developer", UserRole.Admin, false)]
    [InlineData("admin", UserRole.Viewer, true)]
    [InlineData("admin", UserRole.Developer, true)]
    [InlineData("admin", UserRole.Admin, true)]
    [InlineData("agent", UserRole.Viewer, false)]
    public async Task Role_ladder(string role, UserRole minimum, bool expected) =>
        Assert.Equal(expected, await Passes(Handler(oidcConfigured: true), minimum, UserWith(role)));

    [Fact]
    public async Task Anonymous_and_roleless_users_fail_when_configured()
    {
        var handler = Handler(oidcConfigured: true);
        Assert.False(await Passes(handler, UserRole.Viewer, Anonymous));
        Assert.False(await Passes(handler, UserRole.Viewer, UserWith()));
    }

    [Fact]
    public async Task Open_mode_admits_anyone() =>
        Assert.True(await Passes(Handler(oidcConfigured: false), UserRole.Admin, Anonymous));

    [Fact]
    public async Task Api_token_alone_closes_open_mode()
    {
        var handler = Handler(oidcConfigured: false, apiToken: "secret");
        Assert.False(handler.OpenMode);
        Assert.False(await Passes(handler, UserRole.Viewer, Anonymous));
        Assert.True(await Passes(handler, UserRole.Admin, UserWith("admin")));
    }

    [Fact]
    public void Level_reports_the_highest_role()
    {
        Assert.Equal(UserRole.Admin, RoleRequirementHandler.Level(UserWith("viewer", "admin")));
        Assert.Null(RoleRequirementHandler.Level(UserWith("agent")));
        Assert.Equal("developer", Roles.Of(UserRole.Developer));
    }
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test tests/Cicd.Server.Tests`
Expected: build errors for `OidcOptions`, `RoleRequirement`, `RoleRequirementHandler`.

- [ ] **Step 4: Implement options, roles and the requirement**

`src/Cicd.Server/Security/OidcOptions.cs`:

```csharp
namespace Cicd.Server.Security;

/// <summary>Settings for the company OpenID Connect provider. When not configured the server runs in open mode.</summary>
public sealed class OidcOptions
{
    public const string SectionName = "Oidc";
    public string Authority { get; set; } = "";
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    public string Scopes { get; set; } = "openid profile email";
    /// <summary>Validate the audience of bearer JWTs. Off until the IdP has an API resource for CICD.</summary>
    public bool ValidateAudience { get; set; }
    public string Audience { get; set; } = "";
    public bool RequireHttpsMetadata { get; set; } = true;
    /// <summary>Use pushed authorization requests when the IdP advertises them. Set false only for local smoke tests with a placeholder client.</summary>
    public bool UsePushedAuthorization { get; set; } = true;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Authority) && !string.IsNullOrWhiteSpace(ClientId);
}
```

`src/Cicd.Server/Security/RoleRequirement.cs`:

```csharp
using System.Security.Claims;
using Cicd.Contracts;
using Cicd.Core.Users;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;

namespace Cicd.Server.Security;

/// <summary>Requires a role of at least <see cref="Minimum"/>. The static API token carries the admin role and passes every level.</summary>
public sealed class RoleRequirement(UserRole minimum) : IAuthorizationRequirement
{
    public UserRole Minimum { get; } = minimum;
}

/// <summary>
/// Succeeds when the principal's highest role meets the minimum. Also succeeds for everyone in open mode (OIDC not
/// configured and no Security:ApiToken), which keeps the pre-login local development behavior.
/// </summary>
public sealed class RoleRequirementHandler(IOptions<OidcOptions> oidc, IOptions<SecurityOptions> security) : AuthorizationHandler<RoleRequirement>
{
    public bool OpenMode => !oidc.Value.IsConfigured && string.IsNullOrEmpty(security.Value.ApiToken);

    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, RoleRequirement requirement)
    {
        if (OpenMode || Level(context.User) >= requirement.Minimum)
        {
            context.Succeed(requirement);
        }
        return Task.CompletedTask;
    }

    /// <summary>The highest role the principal holds, or null for anonymous, agent-only or disabled principals.</summary>
    public static UserRole? Level(ClaimsPrincipal user)
    {
        if (user.IsInRole(Roles.Admin)) return UserRole.Admin;
        if (user.IsInRole(Roles.Developer)) return UserRole.Developer;
        if (user.IsInRole(Roles.Viewer)) return UserRole.Viewer;
        return null;
    }
}
```

In `src/Cicd.Server/Security/TokenAuthentication.cs`:

Replace the `Roles` and `Policies` classes with:

```csharp
public static class Roles
{
    public const string Agent = "agent";
    public const string Viewer = "viewer";
    public const string Developer = "developer";
    public const string Admin = "admin";

    public static string Of(UserRole role) => role switch
    {
        UserRole.Admin => Admin,
        UserRole.Developer => Developer,
        _ => Viewer,
    };
}

public static class Policies
{
    public const string Agent = "Agent";
    public const string Viewer = "Viewer";
    public const string Developer = "Developer";
    public const string Admin = "Admin";
}
```

Add `using Cicd.Contracts;` at the top. Delete the `AdminRequirement` class and the `AdminRequirementHandler` class. In `AddCicdSecurity`, replace the `AddSingleton<IAuthorizationHandler, AdminRequirementHandler>()` line and the `AddAuthorizationBuilder()` chain with:

```csharp
        services.Configure<OidcOptions>(configuration.GetSection(OidcOptions.SectionName));
        services.AddSingleton<IAuthorizationHandler, RoleRequirementHandler>();
        services.AddAuthorizationBuilder()
            .AddPolicy(Policies.Agent, policy => policy.RequireRole(Roles.Agent))
            .AddPolicy(Policies.Viewer, policy => policy.AddRequirements(new RoleRequirement(UserRole.Viewer)))
            .AddPolicy(Policies.Developer, policy => policy.AddRequirements(new RoleRequirement(UserRole.Developer)))
            .AddPolicy(Policies.Admin, policy => policy.AddRequirements(new RoleRequirement(UserRole.Admin)));
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/Cicd.Server.Tests`
Expected: `Passed! - Failed: 0, Passed: 14`.

- [ ] **Step 6: Build the whole solution, run all tests, commit**

Run: `dotnet build Cicd.slnx && dotnet test Cicd.slnx`
Expected: 0 errors; Core 37 and Server 14 pass.

```bash
git add -A && git commit -m "Add role ladder, RoleRequirement and OidcOptions; start server test project

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 5: `SchemeSelector`

**Files:**
- Create: `src/Cicd.Server/Security/SchemeSelector.cs`
- Modify: `src/Cicd.Server/Security/TokenAuthentication.cs` (`ExtractToken` delegates to the selector)
- Create: `tests/Cicd.Server.Tests/SchemeSelectorTests.cs`

**Interfaces:**
- Produces: `SchemeSelector.SchemeName = "Smart"`; `static string Select(HttpRequest request, bool oidcConfigured)`; `static string? PresentedToken(HttpRequest request)`; `static bool LooksLikeJwt(string token)`.

- [ ] **Step 1: Write the failing tests**

`tests/Cicd.Server.Tests/SchemeSelectorTests.cs`:

```csharp
using Cicd.Server.Security;
using Microsoft.AspNetCore.Http;

namespace Cicd.Server.Tests;

public class SchemeSelectorTests
{
    private const string Jwt = "eyJhbGciOiJSUzI1NiJ9.eyJzdWIiOiIxIn0.c2ln";

    private static HttpRequest Request(Action<HttpRequest>? configure = null)
    {
        var request = new DefaultHttpContext().Request;
        configure?.Invoke(request);
        return request;
    }

    [Fact]
    public void No_credential_is_a_cookie_request() =>
        Assert.Equal("Cookies", SchemeSelector.Select(Request(), oidcConfigured: true));

    [Fact]
    public void Jwt_bearer_goes_to_the_idp_validator() =>
        Assert.Equal("Bearer", SchemeSelector.Select(Request(r => r.Headers.Authorization = $"Bearer {Jwt}"), oidcConfigured: true));

    [Fact]
    public void Opaque_bearer_is_a_static_token() =>
        Assert.Equal("Token", SchemeSelector.Select(Request(r => r.Headers.Authorization = "Bearer dev-token"), oidcConfigured: true));

    [Fact]
    public void Api_key_header_is_a_static_token() =>
        Assert.Equal("Token", SchemeSelector.Select(Request(r => r.Headers["X-Api-Key"] = "dev-token"), oidcConfigured: true));

    [Fact]
    public void Hub_query_token_follows_its_shape()
    {
        Assert.Equal("Bearer", SchemeSelector.Select(Request(r => { r.Path = "/hubs/builds"; r.QueryString = new QueryString($"?access_token={Jwt}"); }), oidcConfigured: true));
        Assert.Equal("Token", SchemeSelector.Select(Request(r => { r.Path = "/hubs/agents"; r.QueryString = new QueryString("?access_token=dev"); }), oidcConfigured: true));
    }

    [Fact]
    public void Query_token_outside_hubs_is_ignored() =>
        Assert.Equal("Cookies", SchemeSelector.Select(Request(r => { r.Path = "/api/v1/builds"; r.QueryString = new QueryString("?access_token=dev"); }), oidcConfigured: true));

    [Fact]
    public void Jwt_without_oidc_falls_back_to_the_static_token_handler() =>
        Assert.Equal("Token", SchemeSelector.Select(Request(r => r.Headers.Authorization = $"Bearer {Jwt}"), oidcConfigured: false));

    [Theory]
    [InlineData(Jwt, true)]
    [InlineData("dev", false)]
    [InlineData("a.b", false)]
    [InlineData("a..c", false)]
    [InlineData("a.b.c.d", false)]
    [InlineData("a b.c.d", false)]
    public void Jwt_shape(string token, bool expected) => Assert.Equal(expected, SchemeSelector.LooksLikeJwt(token));
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Cicd.Server.Tests --filter SchemeSelectorTests`
Expected: build error, `SchemeSelector` not found.

- [ ] **Step 3: Implement the selector and reuse it in the token handler**

`src/Cicd.Server/Security/SchemeSelector.cs`:

```csharp
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;

namespace Cicd.Server.Security;

/// <summary>
/// Picks the authentication scheme for a request. Bearer tokens shaped like JWTs go to the IdP validator, any other
/// bearer or X-Api-Key goes to the static token handler, and everything else is a browser session cookie.
/// </summary>
public static class SchemeSelector
{
    public const string SchemeName = "Smart";

    public static string Select(HttpRequest request, bool oidcConfigured)
    {
        var token = PresentedToken(request);
        if (token is null)
        {
            return CookieAuthenticationDefaults.AuthenticationScheme;
        }
        return oidcConfigured && LooksLikeJwt(token) ? JwtBearerDefaults.AuthenticationScheme : TokenAuthenticationHandler.SchemeName;
    }

    /// <summary>The credential a non-browser client sent: bearer header, X-Api-Key, or the hub access_token query.</summary>
    public static string? PresentedToken(HttpRequest request)
    {
        var header = request.Headers.Authorization.ToString();
        if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return header["Bearer ".Length..].Trim();
        }
        if (request.Headers.TryGetValue("X-Api-Key", out var apiKey) && !string.IsNullOrEmpty(apiKey))
        {
            return apiKey.ToString();
        }
        if (request.Path.StartsWithSegments("/hubs") && request.Query.TryGetValue("access_token", out var queryToken) && !string.IsNullOrEmpty(queryToken))
        {
            return queryToken.ToString();
        }
        return null;
    }

    /// <summary>Three non-empty base64url segments separated by dots. Static tokens never contain dots.</summary>
    public static bool LooksLikeJwt(string token)
    {
        var parts = token.Split('.');
        return parts.Length == 3 && parts.All(p => p.Length > 0 && p.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'));
    }
}
```

In `TokenAuthentication.cs`, replace the body of `ExtractToken` so the method becomes:

```csharp
    private string? ExtractToken() => SchemeSelector.PresentedToken(Request);
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/Cicd.Server.Tests --filter SchemeSelectorTests`
Expected: `Passed! - Failed: 0, Passed: 13`.

- [ ] **Step 5: Commit**

```bash
dotnet build Cicd.slnx && git add -A && git commit -m "Add SchemeSelector for cookie, IdP bearer and static token requests

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 6: `LocalUserClaimsTransformation`

**Files:**
- Create: `src/Cicd.Server/Security/LocalUserClaimsTransformation.cs`
- Create: `tests/Cicd.Server.Tests/TestDb.cs`, `tests/Cicd.Server.Tests/TestClock.cs`, `tests/Cicd.Server.Tests/LocalUserClaimsTransformationTests.cs`

**Interfaces:**
- Consumes: `UserService`, `ExternalIdentity`, `SecurityOptions` (Task 2), `Roles.Of` (Task 4), `TokenAuthenticationHandler.SchemeName`.
- Produces: `LocalUserClaimsTransformation(UserService users, IHttpContextAccessor accessor) : IClaimsTransformation`; constants `UserIdClaim = "cicd:user_id"`, `DisabledClaim = "cicd:disabled"`.

- [ ] **Step 1: Copy the test helpers into the server test project**

`tests/Cicd.Server.Tests/TestDb.cs` and `tests/Cicd.Server.Tests/TestClock.cs`: identical to the Core test versions from Tasks 1 and 2, with `namespace Cicd.Server.Tests;`.

- [ ] **Step 2: Write the failing tests**

`tests/Cicd.Server.Tests/LocalUserClaimsTransformationTests.cs`:

```csharp
using System.Security.Claims;
using Cicd.Contracts;
using Cicd.Core.Entities;
using Cicd.Core.Persistence;
using Cicd.Core.Users;
using Cicd.Server.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Cicd.Server.Tests;

public class LocalUserClaimsTransformationTests : IDisposable
{
    private readonly TestDb testDb = new();
    private readonly TestClock clock = new();

    private (LocalUserClaimsTransformation Transformation, CicdDbContext Db, HttpContextAccessor Accessor) Build(params string[] bootstrapAdmins)
    {
        var db = testDb.Create();
        var users = new UserService(db, Options.Create(new SecurityOptions { BootstrapAdmins = [.. bootstrapAdmins] }), clock);
        var accessor = new HttpContextAccessor { HttpContext = new DefaultHttpContext() };
        return (new LocalUserClaimsTransformation(users, accessor), db, accessor);
    }

    private static ClaimsPrincipal IdpPrincipal(string scheme = "Bearer", string subject = "sub-1") =>
        new(new ClaimsIdentity(
            [new Claim("iss", "https://idp"), new Claim("sub", subject), new Claim("email", "alice@example.com"), new Claim("name", "Alice")],
            scheme));

    [Fact]
    public async Task Idp_identity_gets_a_local_user_and_the_viewer_role()
    {
        var (transformation, db, _) = Build();
        var result = await transformation.TransformAsync(IdpPrincipal());
        Assert.True(result.IsInRole("viewer"));
        Assert.Equal("Alice", result.Identity!.Name);
        var user = await db.Users.SingleAsync();
        Assert.Equal(user.Id.ToString(), result.FindFirst(LocalUserClaimsTransformation.UserIdClaim)!.Value);
        Assert.Equal("https://idp", user.Issuer);
    }

    [Fact]
    public async Task Bootstrap_admin_gets_the_admin_role()
    {
        var (transformation, _, _) = Build("alice@example.com");
        var result = await transformation.TransformAsync(IdpPrincipal());
        Assert.True(result.IsInRole("admin"));
    }

    [Fact]
    public async Task Disabled_user_gets_no_role_and_a_disabled_marker()
    {
        var (transformation, db, _) = Build();
        db.Users.Add(new User { Issuer = "https://idp", Subject = "sub-1", Disabled = true, Role = UserRole.Admin });
        await db.SaveChangesAsync();
        var result = await transformation.TransformAsync(IdpPrincipal());
        Assert.False(result.IsInRole("admin"));
        Assert.False(result.IsInRole("viewer"));
        Assert.True(result.HasClaim(LocalUserClaimsTransformation.DisabledClaim, "true"));
    }

    [Fact]
    public async Task Static_token_and_anonymous_principals_pass_through_unchanged()
    {
        var (transformation, db, _) = Build();
        var token = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, "admin")], TokenAuthenticationHandler.SchemeName));
        Assert.Same(token, await transformation.TransformAsync(token));
        var anonymous = new ClaimsPrincipal(new ClaimsIdentity());
        Assert.Same(anonymous, await transformation.TransformAsync(anonymous));
        Assert.Equal(0, await db.Users.CountAsync());
    }

    [Fact]
    public async Task Runs_once_per_request()
    {
        var (transformation, db, _) = Build();
        var first = await transformation.TransformAsync(IdpPrincipal());
        var second = await transformation.TransformAsync(IdpPrincipal());
        Assert.Same(first, second);
        Assert.Equal(1, await db.Users.CountAsync());
    }

    [Fact]
    public async Task Works_without_an_http_context()
    {
        var (transformation, _, accessor) = Build();
        accessor.HttpContext = null;
        var result = await transformation.TransformAsync(IdpPrincipal());
        Assert.True(result.IsInRole("viewer"));
    }

    public void Dispose() => testDb.Dispose();
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test tests/Cicd.Server.Tests --filter LocalUserClaimsTransformationTests`
Expected: build error, `LocalUserClaimsTransformation` not found.

- [ ] **Step 4: Implement the transformation**

`src/Cicd.Server/Security/LocalUserClaimsTransformation.cs`:

```csharp
using System.Security.Claims;
using Cicd.Core.Users;
using Microsoft.AspNetCore.Authentication;

namespace Cicd.Server.Security;

/// <summary>
/// Turns an IdP identity (session cookie or bearer JWT) into a CICD user: loads or creates the local row by issuer and
/// subject, then adds the role claims authorization runs on. Runs once per request. Static-token identities pass through.
/// </summary>
public sealed class LocalUserClaimsTransformation(UserService users, IHttpContextAccessor accessor) : IClaimsTransformation
{
    public const string UserIdClaim = "cicd:user_id";
    public const string DisabledClaim = "cicd:disabled";
    private const string CacheKey = "cicd:transformed_principal";

    public async Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        var http = accessor.HttpContext;
        if (http?.Items.TryGetValue(CacheKey, out var cached) == true && cached is ClaimsPrincipal done)
        {
            return done;
        }
        var result = await ResolveAsync(principal, http?.RequestAborted ?? CancellationToken.None);
        if (http is not null)
        {
            http.Items[CacheKey] = result;
        }
        return result;
    }

    private async Task<ClaimsPrincipal> ResolveAsync(ClaimsPrincipal principal, CancellationToken cancellationToken)
    {
        if (principal.Identity is not ClaimsIdentity { IsAuthenticated: true } identity
            || identity.AuthenticationType == TokenAuthenticationHandler.SchemeName
            || identity.HasClaim(c => c.Type == UserIdClaim))
        {
            return principal;
        }
        var subject = identity.FindFirst("sub") ?? identity.FindFirst(ClaimTypes.NameIdentifier);
        if (subject is null)
        {
            return principal;
        }
        var external = new ExternalIdentity(
            identity.FindFirst("iss")?.Value ?? subject.Issuer,
            subject.Value,
            identity.FindFirst("username")?.Value ?? identity.FindFirst("preferred_username")?.Value,
            identity.FindFirst("email")?.Value,
            identity.FindFirst("name")?.Value);
        var user = await users.EnsureUserAsync(external, cancellationToken);

        var enriched = new ClaimsIdentity(identity.Claims, identity.AuthenticationType, ClaimTypes.Name, ClaimTypes.Role);
        enriched.AddClaim(new Claim(UserIdClaim, user.Id.ToString()));
        enriched.AddClaim(new Claim(ClaimTypes.Name, user.DisplayName ?? user.Username ?? user.Email ?? user.Subject));
        if (user.Disabled)
        {
            enriched.AddClaim(new Claim(DisabledClaim, "true"));
        }
        else
        {
            enriched.AddClaim(new Claim(ClaimTypes.Role, Roles.Of(user.Role)));
        }
        return new ClaimsPrincipal(enriched);
    }
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/Cicd.Server.Tests --filter LocalUserClaimsTransformationTests`
Expected: `Passed! - Failed: 0, Passed: 6`.

- [ ] **Step 6: Commit**

```bash
dotnet build Cicd.slnx && git add -A && git commit -m "Add LocalUserClaimsTransformation mapping IdP identities to local roles

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 7: Scheme wiring, OIDC events, login and logout endpoints, Program

**Files:**
- Create: `src/Cicd.Server/Security/SecurityServiceCollectionExtensions.cs`, `src/Cicd.Server/Security/AuthEndpoints.cs`
- Modify: `src/Cicd.Server/Security/TokenAuthentication.cs` (delete the `SecurityServiceCollectionExtensions` class from it)
- Modify: `src/Cicd.Server/Cicd.Server.csproj`, `src/Cicd.Server/Program.cs`, `src/Cicd.Server/appsettings.json`

**Interfaces:**
- Consumes: `SchemeSelector`, `RoleRequirement*`, `LocalUserClaimsTransformation`, `UserService`, `ExternalIdentity`, `OidcOptions`.
- Produces: `AddCicdSecurity(IServiceCollection, IConfiguration)` registering schemes `Smart`, `Token`, `Cookies`, and when configured `Bearer` and `OpenIdConnect`; `MapCicdAuth(IEndpointRouteBuilder)` with `GET /login` and `POST /logout`; `SecurityServiceCollectionExtensions.CookiePrincipal(ExternalIdentity)`.

- [ ] **Step 1: Add package references**

In `src/Cicd.Server/Cicd.Server.csproj`, add to the `<ItemGroup>` with package references:

```xml
    <PackageReference Include="Microsoft.AspNetCore.Authentication.OpenIdConnect" />
    <PackageReference Include="Microsoft.AspNetCore.Authentication.JwtBearer" />
```

- [ ] **Step 2: Write the security registration**

Delete the `SecurityServiceCollectionExtensions` class from `TokenAuthentication.cs` (the file keeps `AgentsOptions`, `Roles`, `Policies`, `TokenAuthenticationHandler`).

Create `src/Cicd.Server/Security/SecurityServiceCollectionExtensions.cs`:

```csharp
using System.Security.Claims;
using Cicd.Contracts;
using Cicd.Core.Users;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace Cicd.Server.Security;

public static class SecurityServiceCollectionExtensions
{
    public static IServiceCollection AddCicdSecurity(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<AgentsOptions>(configuration.GetSection(AgentsOptions.SectionName));
        services.Configure<OidcOptions>(configuration.GetSection(OidcOptions.SectionName));
        var oidc = configuration.GetSection(OidcOptions.SectionName).Get<OidcOptions>() ?? new OidcOptions();

        services.AddHttpContextAccessor();
        services.AddScoped<IClaimsTransformation, LocalUserClaimsTransformation>();

        var authentication = services.AddAuthentication(SchemeSelector.SchemeName)
            .AddPolicyScheme(SchemeSelector.SchemeName, "Cookie, IdP bearer or static token", options =>
                options.ForwardDefaultSelector = context => SchemeSelector.Select(context.Request, oidc.IsConfigured))
            .AddScheme<AuthenticationSchemeOptions, TokenAuthenticationHandler>(TokenAuthenticationHandler.SchemeName, null)
            .AddCookie(CookieAuthenticationDefaults.AuthenticationScheme, ConfigureCookie);

        if (oidc.IsConfigured)
        {
            authentication
                .AddJwtBearer(JwtBearerDefaults.AuthenticationScheme, options => ConfigureJwtBearer(options, oidc))
                .AddOpenIdConnect(OpenIdConnectDefaults.AuthenticationScheme, options => ConfigureOpenIdConnect(options, oidc));
        }

        services.AddSingleton<IAuthorizationHandler, RoleRequirementHandler>();
        services.AddAuthorizationBuilder()
            .AddPolicy(Policies.Agent, policy => policy.RequireRole(Roles.Agent))
            .AddPolicy(Policies.Viewer, policy => policy.AddRequirements(new RoleRequirement(UserRole.Viewer)))
            .AddPolicy(Policies.Developer, policy => policy.AddRequirements(new RoleRequirement(UserRole.Developer)))
            .AddPolicy(Policies.Admin, policy => policy.AddRequirements(new RoleRequirement(UserRole.Admin)));
        return services;
    }

    private static void ConfigureCookie(CookieAuthenticationOptions options)
    {
        options.Cookie.Name = "cicd.session";
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.LoginPath = "/login";
        options.LogoutPath = "/logout";
        options.AccessDeniedPath = "/access-denied";
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.SlidingExpiration = true;
        options.Events.OnRedirectToLogin = context => Respond(context, StatusCodes.Status401Unauthorized);
        options.Events.OnRedirectToAccessDenied = context => Respond(context, StatusCodes.Status403Forbidden);
    }

    /// <summary>API and hub callers get a status code; browsers get the redirect.</summary>
    private static Task Respond(RedirectContext<CookieAuthenticationOptions> context, int statusCode)
    {
        if (context.Request.Path.StartsWithSegments("/api") || context.Request.Path.StartsWithSegments("/hubs"))
        {
            context.Response.StatusCode = statusCode;
        }
        else
        {
            context.Response.Redirect(context.RedirectUri);
        }
        return Task.CompletedTask;
    }

    private static void ConfigureJwtBearer(JwtBearerOptions options, OidcOptions oidc)
    {
        options.Authority = oidc.Authority.TrimEnd('/');
        options.RequireHttpsMetadata = oidc.RequireHttpsMetadata;
        options.MapInboundClaims = false;
        options.TokenValidationParameters.NameClaimType = "name";
        options.TokenValidationParameters.RoleClaimType = ClaimTypes.Role;
        options.TokenValidationParameters.ValidateAudience = oidc.ValidateAudience;
        if (oidc.ValidateAudience)
        {
            options.Audience = oidc.Audience;
        }
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                if (context.Request.Path.StartsWithSegments("/hubs") && context.Request.Query.TryGetValue("access_token", out var token))
                {
                    context.Token = token.ToString();
                }
                return Task.CompletedTask;
            },
        };
    }

    private static void ConfigureOpenIdConnect(OpenIdConnectOptions options, OidcOptions oidc)
    {
        options.Authority = oidc.Authority.TrimEnd('/');
        options.ClientId = oidc.ClientId;
        options.ClientSecret = string.IsNullOrEmpty(oidc.ClientSecret) ? null : oidc.ClientSecret;
        options.RequireHttpsMetadata = oidc.RequireHttpsMetadata;
        options.SignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
        options.ResponseType = OpenIdConnectResponseType.Code;
        options.ResponseMode = OpenIdConnectResponseMode.Query;
        options.UsePkce = true;
        options.SaveTokens = false;
        options.GetClaimsFromUserInfoEndpoint = true;
        options.MapInboundClaims = false;
        options.TokenValidationParameters.NameClaimType = "name";
        options.TokenValidationParameters.RoleClaimType = ClaimTypes.Role;
        options.Scope.Clear();
        foreach (var scope in oidc.Scopes.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            options.Scope.Add(scope);
        }
        // Query response mode plus Lax cookies keeps the round trip working over plain http://localhost.
        options.CorrelationCookie.SameSite = SameSiteMode.Lax;
        options.NonceCookie.SameSite = SameSiteMode.Lax;
        if (!oidc.UsePushedAuthorization)
        {
            options.PushedAuthorizationBehavior = PushedAuthorizationBehavior.Disable;
        }
        options.Events.OnTicketReceived = OnTicketReceivedAsync;
    }

    /// <summary>Runs after id_token and userinfo claims are merged. Upserts the local user and issues a slim cookie principal.</summary>
    private static async Task OnTicketReceivedAsync(TicketReceivedContext context)
    {
        var principal = context.Principal ?? throw new InvalidOperationException("OIDC ticket has no principal.");
        var subject = principal.FindFirst("sub") ?? throw new InvalidOperationException("OIDC ticket has no 'sub' claim.");
        var identity = new ExternalIdentity(
            principal.FindFirst("iss")?.Value ?? subject.Issuer,
            subject.Value,
            principal.FindFirst("username")?.Value ?? principal.FindFirst("preferred_username")?.Value,
            principal.FindFirst("email")?.Value,
            principal.FindFirst("name")?.Value);
        var users = context.HttpContext.RequestServices.GetRequiredService<UserService>();
        await users.EnsureUserAsync(identity, context.HttpContext.RequestAborted);
        context.Principal = CookiePrincipal(identity);
    }

    /// <summary>Only identity claims go into the cookie. Role claims are added per request by <see cref="LocalUserClaimsTransformation"/>.</summary>
    public static ClaimsPrincipal CookiePrincipal(ExternalIdentity identity)
    {
        var claims = new List<Claim> { new("iss", identity.Issuer), new("sub", identity.Subject) };
        if (identity.Username is not null) claims.Add(new Claim("username", identity.Username));
        if (identity.Email is not null) claims.Add(new Claim("email", identity.Email));
        if (identity.DisplayName is not null) claims.Add(new Claim("name", identity.DisplayName));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme, "name", ClaimTypes.Role));
    }
}
```

- [ ] **Step 3: Write the login and logout endpoints**

`src/Cicd.Server/Security/AuthEndpoints.cs`:

```csharp
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Options;

namespace Cicd.Server.Security;

public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapCicdAuth(this IEndpointRouteBuilder app)
    {
        app.MapGet("/login", (string? returnUrl, IOptions<OidcOptions> oidc) =>
        {
            if (!oidc.Value.IsConfigured)
            {
                return Results.NotFound();
            }
            var target = returnUrl is not null && returnUrl.StartsWith('/') && !returnUrl.StartsWith("//", StringComparison.Ordinal) ? returnUrl : "/";
            return Results.Challenge(new AuthenticationProperties { RedirectUri = target }, [OpenIdConnectDefaults.AuthenticationScheme]);
        }).ExcludeFromDescription();

        app.MapPost("/logout", async (HttpContext http, IAntiforgery antiforgery, IOptions<OidcOptions> oidc) =>
        {
            if (!await antiforgery.IsRequestValidAsync(http))
            {
                return Results.BadRequest("Invalid antiforgery token.");
            }
            string[] schemes = oidc.Value.IsConfigured
                ? [CookieAuthenticationDefaults.AuthenticationScheme, OpenIdConnectDefaults.AuthenticationScheme]
                : [CookieAuthenticationDefaults.AuthenticationScheme];
            return Results.SignOut(new AuthenticationProperties { RedirectUri = "/" }, schemes);
        }).ExcludeFromDescription();
        return app;
    }
}
```

- [ ] **Step 4: Wire Program.cs and appsettings**

In `src/Cicd.Server/Program.cs`:

After `builder.Services.AddRazorComponents().AddInteractiveServerComponents();` add:

```csharp
builder.Services.AddCascadingAuthenticationState();
```

Replace the `Security:ApiToken` warning block with:

```csharp
var oidcOptions = app.Services.GetRequiredService<IOptions<OidcOptions>>().Value;
if (!oidcOptions.IsConfigured)
{
    app.Logger.LogWarning(string.IsNullOrEmpty(builder.Configuration["Security:ApiToken"])
        ? "Oidc is not configured and Security:ApiToken is empty: the UI and API are open."
        : "Oidc is not configured: the UI and API accept only the static Security:ApiToken.");
}
```

Replace the last two `Map` lines with:

```csharp
app.MapCicdAuth();
app.MapHub<AgentHub>("/hubs/agents");
app.MapHub<BuildHub>("/hubs/builds").RequireAuthorization(Policies.Viewer);
app.MapRazorComponents<App>().AddInteractiveServerRenderMode().RequireAuthorization(Policies.Viewer);
```

In `src/Cicd.Server/appsettings.json`, replace the `"Security"` object with:

```json
  "Security": {
    "ApiToken": "",
    "BootstrapAdmins": []
  },
  "Oidc": {
    "Authority": "https://identity-dev.manageamerica.com",
    "ClientId": "",
    "ClientSecret": "",
    "Scopes": "openid profile email",
    "ValidateAudience": false,
    "Audience": "",
    "RequireHttpsMetadata": true,
  "UsePushedAuthorization": true
  },
```

- [ ] **Step 5: Build and run the unit tests**

Run: `dotnet build Cicd.slnx && dotnet test Cicd.slnx`
Expected: 0 errors, all tests pass.

- [ ] **Step 6: Smoke test open mode**

Run (from repo root; the `cicd-postgres` container must be running):

```bash
LOG=/tmp/cicd-open.log
ASPNETCORE_ENVIRONMENT=Development DOTNET_ENVIRONMENT=Development Agents__AuthToken=dev dotnet run --project src/Cicd.Server --no-build > $LOG 2>&1 &
PID=$!; for i in $(seq 1 45); do grep -q "Now listening" $LOG && break; sleep 1; done
curl -s -o /dev/null -w 'home %{http_code}\n' http://localhost:5000/
curl -s -o /dev/null -w 'login %{http_code}\n' http://localhost:5000/login
curl -s -o /dev/null -w 'builds %{http_code}\n' http://localhost:5000/api/v1/builds
grep -c "the UI and API are open" $LOG
kill $PID; wait $PID 2>/dev/null
```

Expected: `home 200`, `login 404`, `builds 200`, and `1` warning line.

- [ ] **Step 7: Smoke test configured mode with a placeholder client**

```bash
LOG=/tmp/cicd-oidc.log
ASPNETCORE_ENVIRONMENT=Development DOTNET_ENVIRONMENT=Development Agents__AuthToken=dev Security__ApiToken=admin-dev Oidc__ClientId=smoke Oidc__UsePushedAuthorization=false dotnet run --project src/Cicd.Server --no-build > $LOG 2>&1 &
PID=$!; for i in $(seq 1 45); do grep -q "Now listening" $LOG && break; sleep 1; done
curl -s -o /dev/null -w 'home %{http_code} %{redirect_url}\n' http://localhost:5000/
curl -s -o /dev/null -w 'login %{http_code} %{redirect_url}\n' 'http://localhost:5000/login?returnUrl=/builds'
curl -s -o /dev/null -w 'builds anon %{http_code}\n' http://localhost:5000/api/v1/builds
curl -s -o /dev/null -w 'builds token %{http_code}\n' -H 'Authorization: Bearer admin-dev' http://localhost:5000/api/v1/builds
curl -s -o /dev/null -w 'builds jwt %{http_code}\n' -H 'Authorization: Bearer eyJhbGciOiJSUzI1NiJ9.eyJzdWIiOiIxIn0.c2ln' http://localhost:5000/api/v1/builds
kill $PID; wait $PID 2>/dev/null
```

Expected: `home 302 http://localhost:5000/login?ReturnUrl=%2F`; `login 302` with a redirect URL starting `https://identity-dev.manageamerica.com/connect/authorize?client_id=smoke` and containing `code_challenge_method=S256` (the handler does not emit `response_mode` for query mode); `builds anon 401`; `builds token 200`; `builds jwt 401` (signature cannot validate).

- [ ] **Step 8: Commit**

```bash
git add -A && git commit -m "Wire cookie, OIDC, JwtBearer and static token schemes with login and logout

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 8: Per-endpoint policies and the users API

**Files:**
- Modify: `src/Cicd.Server/Api/ApiEndpoints.cs`

**Interfaces:**
- Consumes: `Policies.*`, `UserService`, `UserDto`, `SetUserRoleRequest`, `SetUserDisabledRequest`, `LocalUserClaimsTransformation.UserIdClaim`.
- Produces: `GET /api/v1/users`, `PUT /api/v1/users/{id}/role`, `PUT /api/v1/users/{id}/disabled` (Admin).

- [ ] **Step 1: Replace group-level policies with per-endpoint policies**

Add `using System.Security.Claims;` and `using Cicd.Core.Users;` to the usings. Replace `MapCicdApi` with:

```csharp
    public static IEndpointRouteBuilder MapCicdApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/v1");

        MapProjects(api.MapGroup("/projects").WithTags("Projects"));
        MapVcsRoots(api.MapGroup("/vcs-roots").WithTags("VCS roots"));
        MapBuildConfigurations(api.MapGroup("/build-configurations").WithTags("Build configurations"));
        MapBuilds(api.MapGroup("/builds").WithTags("Builds"));
        MapAgents(api.MapGroup("/agents").WithTags("Agents"));
        MapPullRequests(api.MapGroup("/pull-requests").WithTags("Pull requests"));
        MapPlugins(api.MapGroup("/plugins").WithTags("Plugins").RequireAuthorization(Policies.Admin));
        MapUsers(api.MapGroup("/users").WithTags("Users").RequireAuthorization(Policies.Admin));
        MapWebhooks(api.MapGroup("/webhooks").WithTags("Webhooks"));
        return app;
    }
```

Then append `.RequireAuthorization(...)` to each endpoint registration, exactly as follows. Every `group.MapGet(...)` in `MapProjects`, `MapVcsRoots`, `MapBuildConfigurations`, `MapAgents` and `MapPullRequests` gets `.RequireAuthorization(Policies.Viewer)`. Every `MapPost`, `MapPut` and `MapDelete` in `MapProjects`, `MapVcsRoots`, `MapBuildConfigurations` and `MapPullRequests` gets `.RequireAuthorization(Policies.Developer)`. In `MapAgents`, `/{id:guid}/authorize`, `/{id:guid}/enable` and `MapDelete` get `.RequireAuthorization(Policies.Admin)`. In `MapBuilds`, change every existing `.RequireAuthorization(Policies.Admin)` on a `MapGet` to `Policies.Viewer`, and on `/{id:guid}/cancel` to `Policies.Developer`; the artifact `MapPost` keeps `Policies.Agent`. `MapWebhooks` is unchanged (`AllowAnonymous`).

Example of the resulting shape for one read and one write in `MapProjects`:

```csharp
        group.MapGet("/", async (CicdDbContext db, CancellationToken ct) =>
            await db.Projects.OrderBy(p => p.Name).Select(p => p.ToDto()).ToListAsync(ct)).RequireAuthorization(Policies.Viewer);

        group.MapDelete("/{id:guid}", async Task<IResult> (Guid id, CicdDbContext db, CancellationToken ct) =>
            await db.Projects.Where(p => p.Id == id).ExecuteDeleteAsync(ct) > 0 ? Results.NoContent() : Results.NotFound()).RequireAuthorization(Policies.Developer);
```

Check with: `grep -c "RequireAuthorization" src/Cicd.Server/Api/ApiEndpoints.cs` — expected 36 (34 per-endpoint policies plus the plugins and users groups; the 5 plugin endpoints rely on their group), and `grep -n "Map\(Get\|Post\|Put\|Delete\)" src/Cicd.Server/Api/ApiEndpoints.cs | grep -v "RequireAuthorization\|AllowAnonymous\|MapGroup"` — expected: only lines that continue on the next line (multi-line lambdas); confirm each of those ends with a policy by reading the closing `})` line.

- [ ] **Step 2: Add the users endpoints**

Append inside `ApiEndpoints` (after `MapPlugins`):

```csharp
    private static void MapUsers(RouteGroupBuilder group)
    {
        group.MapGet("/", async (UserService users, CancellationToken ct) =>
            (await users.ListAsync(ct)).Select(u => u.ToDto()).ToList());

        group.MapPut("/{id:guid}/role", async Task<IResult> (Guid id, SetUserRoleRequest request, UserService users, ClaimsPrincipal caller, CancellationToken ct) =>
            await Guarded(() => users.SetRoleAsync(id, request.Role, ActingUserId(caller), ct)));

        group.MapPut("/{id:guid}/disabled", async Task<IResult> (Guid id, SetUserDisabledRequest request, UserService users, ClaimsPrincipal caller, CancellationToken ct) =>
            await Guarded(() => users.SetDisabledAsync(id, request.Disabled, ActingUserId(caller), ct)));
    }

    private static Guid? ActingUserId(ClaimsPrincipal caller) =>
        Guid.TryParse(caller.FindFirst(LocalUserClaimsTransformation.UserIdClaim)?.Value, out var id) ? id : null;

    /// <summary>Maps a missing user to 404 and a refused change (self-edit, last admin) to 409.</summary>
    private static async Task<IResult> Guarded(Func<Task<User?>> action)
    {
        try
        {
            return await action() is { } user ? Results.Ok(user.ToDto()) : Results.NotFound();
        }
        catch (InvalidOperationException ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict);
        }
    }
```

- [ ] **Step 3: Build and verify with the static token**

```bash
dotnet build Cicd.slnx
LOG=/tmp/cicd-api.log
ASPNETCORE_ENVIRONMENT=Development Agents__AuthToken=dev Security__ApiToken=admin-dev dotnet run --project src/Cicd.Server --no-build > $LOG 2>&1 &
PID=$!; for i in $(seq 1 45); do grep -q "Now listening" $LOG && break; sleep 1; done
H='Authorization: Bearer admin-dev'
curl -s -o /dev/null -w 'users anon %{http_code}\n' http://localhost:5000/api/v1/users
curl -s -w '\nusers admin %{http_code}\n' -H "$H" http://localhost:5000/api/v1/users
curl -s -o /dev/null -w 'role 404 %{http_code}\n' -X PUT -H "$H" -H 'content-type: application/json' -d '{"role":"Admin"}' http://localhost:5000/api/v1/users/00000000-0000-0000-0000-000000000001/role
curl -s -o /dev/null -w 'agent upload w/ admin %{http_code}\n' -X POST -H "$H" 'http://localhost:5000/api/v1/builds/00000000-0000-0000-0000-000000000001/artifacts?path=x' --data-binary 'x'
kill $PID; wait $PID 2>/dev/null
```

Expected: `users anon 401`; `[]` then `users admin 200`; `role 404 404`; `agent upload w/ admin 403` (admin token is not an agent).

- [ ] **Step 4: Commit**

```bash
git add -A && git commit -m "Apply viewer, developer and admin policies per endpoint; add users API

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 9: Blazor UI: routing, sidebar user block, users page, gated actions

**Files:**
- Create: `src/Cicd.Server/Components/SecurePage.cs`, `src/Cicd.Server/Components/NotAuthorizedView.razor`, `src/Cicd.Server/Components/Pages/Users.razor`, `src/Cicd.Server/Components/Pages/AccessDenied.razor`
- Modify: `src/Cicd.Server/Components/_Imports.razor`, `Routes.razor`, `Layout/MainLayout.razor`, `Pages/Error.razor`, `Pages/Projects.razor`, `Pages/ProjectDetail.razor`, `Pages/BuildDetail.razor`, `Pages/Agents.razor`, `src/Cicd.Server/wwwroot/app.css`

**Interfaces:**
- Consumes: `Policies.*`, `RoleRequirementHandler.Level`, `LocalUserClaimsTransformation.{UserIdClaim,DisabledClaim}`, `UserService`, `UserDto`, `OidcOptions`.
- Produces: `abstract class SecurePage : ComponentBase` with `protected Task<bool> AllowedAsync(string policy)` and `protected Task<AuthenticationState> AuthState`.

- [ ] **Step 1: Imports, routing, base class, denied views**

Append to `src/Cicd.Server/Components/_Imports.razor`:

```razor
@using System.Security.Claims
@using Microsoft.AspNetCore.Authorization
@using Microsoft.AspNetCore.Components.Authorization
@using Microsoft.Extensions.Options
@using Cicd.Core.Users
@using Cicd.Server.Security
```

Replace `src/Cicd.Server/Components/Routes.razor` with:

```razor
<Router AppAssembly="typeof(Program).Assembly">
    <Found Context="routeData">
        <AuthorizeRouteView RouteData="routeData" DefaultLayout="typeof(MainLayout)">
            <NotAuthorized>
                <NotAuthorizedView />
            </NotAuthorized>
        </AuthorizeRouteView>
        <FocusOnNavigate RouteData="routeData" Selector="h1" />
    </Found>
</Router>
```

`src/Cicd.Server/Components/NotAuthorizedView.razor`:

```razor
@inject NavigationManager Navigation
<AuthorizeView>
    <Authorized>
        <h1>Access denied</h1>
        <p>Your account does not have the role this page needs. Ask an administrator.</p>
    </Authorized>
    <NotAuthorized>
        <p class="muted">Redirecting to sign in…</p>
    </NotAuthorized>
</AuthorizeView>

@code {
    [CascadingParameter] private Task<AuthenticationState>? AuthState { get; set; }

    protected override async Task OnParametersSetAsync()
    {
        if (AuthState is null) return;
        var state = await AuthState;
        if (state.User.Identity?.IsAuthenticated != true)
        {
            var returnUrl = "/" + Navigation.ToBaseRelativePath(Navigation.Uri);
            Navigation.NavigateTo($"/login?returnUrl={Uri.EscapeDataString(returnUrl)}", forceLoad: true);
        }
    }
}
```

`src/Cicd.Server/Components/SecurePage.cs`:

```csharp
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;

namespace Cicd.Server.Components;

/// <summary>Base for pages with role-gated actions. Buttons hide through AuthorizeView; the action re-checks the policy server-side.</summary>
public abstract class SecurePage : ComponentBase
{
    [CascadingParameter] protected Task<AuthenticationState> AuthState { get; set; } = default!;
    [Inject] protected IAuthorizationService Authorization { get; set; } = default!;

    protected async Task<bool> AllowedAsync(string policy)
    {
        var state = await AuthState;
        return (await Authorization.AuthorizeAsync(state.User, policy)).Succeeded;
    }
}
```

`src/Cicd.Server/Components/Pages/AccessDenied.razor`:

```razor
@page "/access-denied"
@attribute [AllowAnonymous]
<PageTitle>Access denied - CICD</PageTitle>
<h1>Access denied</h1>
<AuthorizeView>
    <Authorized>
        @if (context.User.HasClaim(LocalUserClaimsTransformation.DisabledClaim, "true"))
        {
            <p>Your account is disabled. Ask an administrator to enable it.</p>
        }
        else
        {
            <p>Your role does not allow this. Ask an administrator for the developer or admin role.</p>
        }
    </Authorized>
    <NotAuthorized>
        <p><a href="/login">Sign in</a> to continue.</p>
    </NotAuthorized>
</AuthorizeView>
```

In `src/Cicd.Server/Components/Pages/Error.razor`, add `@attribute [AllowAnonymous]` directly under the `@page` line.

- [ ] **Step 2: Sidebar user block and styles**

Replace `src/Cicd.Server/Components/Layout/MainLayout.razor` with:

```razor
@inherits LayoutComponentBase
@inject IOptions<OidcOptions> Oidc
<div class="shell">
    <nav class="sidebar">
        <a class="brand" href="/">CICD</a>
        <NavLink href="/" Match="NavLinkMatch.All">Overview</NavLink>
        <NavLink href="/projects">Projects</NavLink>
        <NavLink href="/builds">Builds</NavLink>
        <NavLink href="/agents">Agents</NavLink>
        <NavLink href="/plugins">Plugins</NavLink>
        <AuthorizeView Policy="@Policies.Admin">
            <NavLink href="/users">Users</NavLink>
        </AuthorizeView>
        <a href="/api/docs" target="_blank" rel="noopener">API docs</a>
        <div class="sidebar-user">
            <AuthorizeView>
                <Authorized>
                    <span title="@RoleOf(context.User)">@context.User.Identity?.Name</span>
                    <form method="post" action="/logout">
                        <AntiforgeryToken />
                        <button type="submit" class="link">Sign out</button>
                    </form>
                </Authorized>
                <NotAuthorized>
                    @if (Oidc.Value.IsConfigured)
                    {
                        <a href="/login">Sign in</a>
                    }
                    else
                    {
                        <span class="muted small">Open mode: no login configured</span>
                    }
                </NotAuthorized>
            </AuthorizeView>
        </div>
    </nav>
    <main class="content">
        @Body
    </main>
</div>

@code {
    private static string RoleOf(ClaimsPrincipal user) => RoleRequirementHandler.Level(user)?.ToString() ?? "no role";
}
```

Append to `src/Cicd.Server/wwwroot/app.css`:

```css
.sidebar-user { margin-top: auto; padding: 12px 20px; border-top: 1px solid var(--line); display: flex; flex-direction: column; gap: 6px; }
.sidebar-user a { padding: 0; } .sidebar-user form { margin: 0; }
button.link { background: none; color: var(--accent); padding: 0; }
select { background: var(--bg); color: var(--text); border: 1px solid var(--line); padding: 4px 8px; border-radius: 4px; }
```

- [ ] **Step 3: Users page**

`src/Cicd.Server/Components/Pages/Users.razor`:

```razor
@page "/users"
@attribute [Authorize(Policy = Policies.Admin)]
@inherits SecurePage
@inject IServiceScopeFactory Scopes

<PageTitle>Users - CICD</PageTitle>
<h1>Users</h1>
<p class="muted">Everyone who has signed in. New users start as viewers; bootstrap admins are set in configuration.</p>
@if (error is not null)
{
    <div class="error">@error</div>
}
<table>
    <thead><tr><th>Name</th><th>Username</th><th>Email</th><th>Role</th><th>Last seen</th><th></th></tr></thead>
    <tbody>
    @foreach (var u in users)
    {
        <tr class="@(u.Disabled ? "muted" : "")">
            <td>@u.DisplayName</td>
            <td>@u.Username</td>
            <td>@u.Email</td>
            <td>
                <select value="@u.Role" disabled="@u.Disabled" @onchange="e => SetRoleAsync(u.Id, Enum.Parse<UserRole>((string)e.Value!))">
                    @foreach (var role in Enum.GetValues<UserRole>())
                    {
                        <option value="@role">@role</option>
                    }
                </select>
            </td>
            <td>@u.LastSeenAt?.LocalDateTime.ToString("g")</td>
            <td><button @onclick="() => SetDisabledAsync(u.Id, !u.Disabled)">@(u.Disabled ? "Enable" : "Disable")</button></td>
        </tr>
    }
    </tbody>
</table>

@code {
    private List<UserDto> users = [];
    private string? error;

    protected override Task OnInitializedAsync() => LoadAsync();

    private async Task LoadAsync()
    {
        await using var scope = Scopes.CreateAsyncScope();
        users = (await scope.ServiceProvider.GetRequiredService<UserService>().ListAsync(CancellationToken.None)).Select(u => u.ToDto()).ToList();
    }

    private Task SetRoleAsync(Guid id, UserRole role) =>
        RunAsync((service, me) => service.SetRoleAsync(id, role, me, CancellationToken.None));

    private Task SetDisabledAsync(Guid id, bool disabled) =>
        RunAsync((service, me) => service.SetDisabledAsync(id, disabled, me, CancellationToken.None));

    private async Task RunAsync(Func<UserService, Guid?, Task> action)
    {
        error = null;
        if (!await AllowedAsync(Policies.Admin)) { error = "Admin role required."; return; }
        var me = Guid.TryParse((await AuthState).User.FindFirst(LocalUserClaimsTransformation.UserIdClaim)?.Value, out var id) ? id : (Guid?)null;
        await using var scope = Scopes.CreateAsyncScope();
        try
        {
            await action(scope.ServiceProvider.GetRequiredService<UserService>(), me);
        }
        catch (InvalidOperationException ex)
        {
            error = ex.Message;
        }
        await LoadAsync();
    }
}
```

- [ ] **Step 4: Gate actions on the existing pages**

`Pages/Projects.razor`: add `@inherits SecurePage` after the `@page` line. Wrap the create form in `<AuthorizeView Policy="@Policies.Developer" Context="auth"> ... </AuthorizeView>`. At the top of the method that creates the project add `if (!await AllowedAsync(Policies.Developer)) return;`.

`Pages/ProjectDetail.razor`: add `@inherits SecurePage`. Replace the Run cell with:

```razor
                <td><AuthorizeView Policy="@Policies.Developer" Context="auth"><button @onclick="() => QueueAsync(c.Id)" disabled="@c.Paused">Run</button></AuthorizeView></td>
```

and start `QueueAsync` with `if (!await AllowedAsync(Policies.Developer)) return;`.

`Pages/BuildDetail.razor`: add `@inherits SecurePage`. Wrap the Cancel button: `<AuthorizeView Policy="@Policies.Developer" Context="auth"><button @onclick="CancelAsync">Cancel</button></AuthorizeView>` and start `CancelAsync` with `if (!await AllowedAsync(Policies.Developer)) return;`.

`Pages/Agents.razor`: add `@inherits SecurePage`. Wrap each of the two buttons in `<AuthorizeView Policy="@Policies.Admin" Context="auth"> ... </AuthorizeView>` and start `AuthorizeAsync` and `EnableAsync` with `if (!await AllowedAsync(Policies.Admin)) return;`.

If a page already declares `@implements IDisposable`, keep it; `@inherits` and `@implements` combine.

- [ ] **Step 5: Build and check both modes in a browser**

Run `dotnet build Cicd.slnx` (0 errors). Then open mode:

```bash
ASPNETCORE_ENVIRONMENT=Development Agents__AuthToken=dev dotnet run --project src/Cicd.Server --no-build
```

In a browser at http://localhost:5000: the sidebar shows "Open mode: no login configured" and a Users link; `/users` renders an empty table; `/projects` shows the create form; `/access-denied` renders. Stop the server.

Configured mode with a placeholder client:

```bash
ASPNETCORE_ENVIRONMENT=Development Agents__AuthToken=dev Oidc__ClientId=smoke dotnet run --project src/Cicd.Server --no-build
```

http://localhost:5000/ redirects to `/login?ReturnUrl=%2F` and then to identity-dev's authorize page (which will reject the unknown client, which is expected). `/access-denied` renders with the Sign in link. Stop the server.

- [ ] **Step 6: Commit**

```bash
git add -A && git commit -m "Add auth-aware routing, sidebar user block, users page and role-gated actions

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 10: Documentation and handoff

**Files:**
- Modify: `README.md`, `docs/ARCHITECTURE.md`, `HANDOFF.md`

- [ ] **Step 1: README**

In the configuration table replace the `Security:ApiToken` row and add rows:

```markdown
| `Security:ApiToken` | Static bearer token that acts as an admin. Break-glass and automation. Empty disables it. |
| `Security:BootstrapAdmins` | Emails promoted to admin on every sign-in. Set at least one before the first login. |
| `Oidc:Authority` / `Oidc:ClientId` / `Oidc:ClientSecret` | The OpenID Connect provider. With `ClientId` empty the server runs in open mode (no login; UI and API open unless `Security:ApiToken` is set). |
| `Oidc:Scopes` | Default `openid profile email`. |
| `Oidc:ValidateAudience` / `Oidc:Audience` | Audience validation for bearer JWTs. Off until the IdP has an API resource for CICD. |
```

Add a section after "Configuration":

```markdown
## Users and roles

Sign-in is OpenID Connect (authorization code + PKCE) against `Oidc:Authority`. On first sign-in a user row is
created with the `viewer` role; emails listed in `Security:BootstrapAdmins` become `admin`. Roles are global:

| Role | Can |
| --- | --- |
| viewer | read everything: builds, logs, artifacts, projects, agents |
| developer | viewer plus queue and cancel builds, create and edit projects, VCS roots and configurations |
| admin | developer plus authorize and delete agents, manage users, list plugins |

Admins change roles on `/users` or with `PUT /api/v1/users/{id}/role`. Changes apply on the next request.
API clients send an IdP access token as `Authorization: Bearer <jwt>`; the same local user and role apply.
`Security:ApiToken` is a static admin credential for automation. Agents use `Agents:AuthToken` only.

Register a client at the IdP with redirect URI `<Server:PublicUrl>/signin-oidc` (and `http://localhost:5000/signin-oidc`
for development), post-logout redirect `<Server:PublicUrl>/signout-callback-oidc`, and scopes `openid profile email`.
```

In "What is deliberately not here yet", replace the users bullet with:
`- **Per-project roles, groups, personal access tokens.** Roles are global for now.`

- [ ] **Step 2: ARCHITECTURE.md**

Replace the "Security model (current)" section with:

```markdown
## Security model

- **Schemes.** A policy scheme (`SchemeSelector`) forwards each request: a JWT-shaped bearer or hub `access_token` goes
  to JwtBearer (validated against the IdP), any other bearer or `X-Api-Key` goes to `TokenAuthenticationHandler`
  (agent token -> role `agent`, `Security:ApiToken` -> role `admin`), everything else is the session cookie issued
  after the OIDC login (`/login` challenges, `/logout` signs out of the cookie and the IdP).
- **Local users.** `LocalUserClaimsTransformation` runs on every cookie or JWT request, upserts the `users` row by
  issuer and subject through `UserService`, and adds `cicd:user_id` plus a role claim. Disabled users get no role.
- **Policies.** `Viewer` < `Developer` < `Admin` through `RoleRequirement`; `Agent` requires the agent role. In open
  mode (no `Oidc:ClientId` and no `Security:ApiToken`) every role policy succeeds so local development needs no login.
- Webhooks stay anonymous at the HTTP layer; handlers verify provider signatures.
```

- [ ] **Step 3: HANDOFF.md**

In "What is done and verified" add a row:
`| Users, roles, OIDC login | code and unit tests done; **OIDC round trip not exercised** | needs an IdP client registration; see README "Users and roles" |`

Replace gap 2 ("Users, roles, login") with:
`2. **First real login.** Register the CICD client at identity-dev, set Oidc:ClientId and Security:BootstrapAdmins, sign in, confirm the bootstrap admin lands on /users, then verify a JWT against /api/v1/builds and /hubs/builds.`

- [ ] **Step 4: Final verification and commit**

Run: `dotnet build Cicd.slnx && dotnet test Cicd.slnx`
Expected: 0 errors; Core 37 and Server 33 tests pass.

```bash
git add -A && git commit -m "Document users, roles and OIDC login

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```
