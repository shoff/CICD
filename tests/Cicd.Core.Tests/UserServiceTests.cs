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
