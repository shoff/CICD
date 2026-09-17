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

    [Fact]
    public async Task Restart_pending_ignores_boolean_casing()
    {
        reloader.Startup["IdentityProvider:ValidateAudience"] = "False";
        await using var db = testDb.Create();
        db.Settings.Add(new Setting { Key = "IdentityProvider:ValidateAudience", Value = "false" });
        await db.SaveChangesAsync();
        var views = await Service(db).GetAllAsync(CancellationToken.None);
        Assert.False(views.Single(v => v.Definition.Key == "IdentityProvider:ValidateAudience").RestartPending);
    }

    [Fact]
    public async Task Restart_pending_compares_lists_against_the_joined_startup_value()
    {
        reloader.Startup["Plugins:Disabled:0"] = "a";
        reloader.Startup["Plugins:Disabled:1"] = "b";
        await using var db = testDb.Create();
        db.Settings.Add(new Setting { Key = "Plugins:Disabled", Value = "a,b" });
        await db.SaveChangesAsync();
        Assert.False((await Service(db).GetAllAsync(CancellationToken.None)).Single(v => v.Definition.Key == "Plugins:Disabled").RestartPending);

        var row = await db.Settings.SingleAsync(s => s.Key == "Plugins:Disabled");
        row.Value = "a";
        await db.SaveChangesAsync();
        Assert.True((await Service(db).GetAllAsync(CancellationToken.None)).Single(v => v.Definition.Key == "Plugins:Disabled").RestartPending);
    }

    public void Dispose() => testDb.Dispose();
}
