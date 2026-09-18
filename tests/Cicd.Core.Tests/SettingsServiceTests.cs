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
    public bool FailReload { get; set; }
    public void Reload()
    {
        Reloads++;
        if (FailReload)
        {
            throw new InvalidOperationException("binder says no");
        }
    }
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

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Validation_rejects_an_empty_number(string? value)
    {
        // The binder cannot read "" as an int: a stored empty number breaks every IOptionsMonitor read of its section.
        var errors = SettingsService.Validate(new Dictionary<string, string?> { ["IdentityProvider:TimeoutSeconds"] = value });
        Assert.Single(errors);
    }

    [Fact]
    public async Task Read_reports_an_undecryptable_secret_without_leaking_it()
    {
        await using var db = testDb.Create();
        db.Settings.Add(new Setting { Key = "Security:ApiToken", Value = "enc:v1:garbage", IsSecret = true });
        await db.SaveChangesAsync();
        var service = new SettingsService(db, new ThrowingProtector(), reloader, clock, NullLogger<SettingsService>.Instance);
        var view = (await service.GetAllAsync(CancellationToken.None)).Single(v => v.Definition.Key == "Security:ApiToken");
        Assert.True(view.Unreadable);
        Assert.False(view.IsSet);
        Assert.Equal("", view.Value);
    }

    [Fact]
    public async Task A_failed_reload_is_reported_as_saved_but_not_reloaded()
    {
        await using var db = testDb.Create();
        reloader.FailReload = true;
        // Its own type: callers tell "stored, not live" (409) apart from any other InvalidOperationException, which
        // EF also throws and which does not mean the values were stored.
        await Assert.ThrowsAsync<SettingsReloadException>(() => Service(db).UpdateAsync(
            new Dictionary<string, string?> { ["Server:PublicUrl"] = "http://x" }, null, CancellationToken.None));
        Assert.Equal("http://x", (await db.Settings.SingleAsync(s => s.Key == "Server:PublicUrl")).Value);
    }

    [Fact]
    public async Task Null_secret_clears_it()
    {
        await using var db = testDb.Create();
        var service = Service(db);
        await service.UpdateAsync(new Dictionary<string, string?> { ["Agents:AuthToken"] = "first" }, null, CancellationToken.None);
        await service.UpdateAsync(new Dictionary<string, string?> { ["Agents:AuthToken"] = null }, null, CancellationToken.None);
        var row = await db.Settings.SingleAsync(s => s.Key == "Agents:AuthToken");
        Assert.Equal("", row.Value);
        Assert.True(row.IsSecret);
        var view = (await service.GetAllAsync(CancellationToken.None)).Single(v => v.Definition.Key == "Agents:AuthToken");
        Assert.False(view.IsSet);
    }

    [Fact]
    public async Task Update_with_invalid_values_writes_nothing()
    {
        await using var db = testDb.Create();
        await Assert.ThrowsAsync<SettingsValidationException>(() => Service(db).UpdateAsync(
            new Dictionary<string, string?> { ["Server:PublicUrl"] = "http://x", ["Server:DispatchIntervalSeconds"] = "x" }, null, CancellationToken.None));
        Assert.Equal(0, await db.Settings.CountAsync());
        Assert.Equal(0, reloader.Reloads);
    }

    [Fact]
    public async Task Audience_validation_without_an_audience_is_rejected()
    {
        await using var db = testDb.Create();
        var error = await Assert.ThrowsAsync<SettingsValidationException>(() => Service(db).UpdateAsync(
            new Dictionary<string, string?> { ["IdentityProvider:ValidateAudience"] = "true", ["IdentityProvider:Audience"] = "" },
            null, CancellationToken.None));
        Assert.Contains("Audience", error.Message);
        Assert.Equal(0, await db.Settings.CountAsync());
        Assert.Equal(0, reloader.Reloads);
    }

    [Fact]
    public async Task Https_requirement_rejects_a_plain_http_authority_or_login_url()
    {
        await using var db = testDb.Create();
        var service = Service(db);
        var authority = await Assert.ThrowsAsync<SettingsValidationException>(() => service.UpdateAsync(
            new Dictionary<string, string?> { ["IdentityProvider:RequireHttpsMetadata"] = "true", ["IdentityProvider:Authority"] = "http://idp" },
            null, CancellationToken.None));
        Assert.Contains("Authority", authority.Message);
        var login = await Assert.ThrowsAsync<SettingsValidationException>(() => service.UpdateAsync(
            new Dictionary<string, string?> { ["IdentityProvider:RequireHttpsMetadata"] = "true", ["IdentityProvider:LoginUrl"] = "http://idp/login" },
            null, CancellationToken.None));
        Assert.Contains("Login URL", login.Message);
        Assert.Equal(0, await db.Settings.CountAsync());
        Assert.Equal(0, reloader.Reloads);
    }

    [Fact]
    public async Task Https_requirement_accepts_https_urls()
    {
        await using var db = testDb.Create();
        await Service(db).UpdateAsync(
            new Dictionary<string, string?>
            {
                ["IdentityProvider:RequireHttpsMetadata"] = "true",
                ["IdentityProvider:Authority"] = "https://idp",
                ["IdentityProvider:LoginUrl"] = "https://idp/login",
            }, null, CancellationToken.None);
        Assert.Equal("https://idp", (await db.Settings.SingleAsync(s => s.Key == "IdentityProvider:Authority")).Value);
        Assert.Equal(1, reloader.Reloads);
    }

    [Fact]
    public async Task Cross_field_rules_do_not_block_a_save_of_another_section()
    {
        await using var db = testDb.Create();
        db.Settings.Add(new Setting { Key = "IdentityProvider:ValidateAudience", Value = "true" });
        db.Settings.Add(new Setting { Key = "IdentityProvider:Audience", Value = "" });
        await db.SaveChangesAsync();
        // The stored identity data violates rule (a), but this payload carries no IdentityProvider key and cannot fix it.
        await Service(db).UpdateAsync(
            new Dictionary<string, string?> { ["Server:PublicUrl"] = "http://x" }, null, CancellationToken.None);
        Assert.Equal("http://x", (await db.Settings.SingleAsync(s => s.Key == "Server:PublicUrl")).Value);
        Assert.Equal(1, reloader.Reloads);
    }

    [Fact]
    public async Task Cross_field_rules_see_the_values_already_stored()
    {
        await using var db = testDb.Create();
        db.Settings.Add(new Setting { Key = "IdentityProvider:RequireHttpsMetadata", Value = "true" });
        await db.SaveChangesAsync();
        // The authority alone is submitted; the requirement it violates is only in the table.
        await Assert.ThrowsAsync<SettingsValidationException>(() => Service(db).UpdateAsync(
            new Dictionary<string, string?> { ["IdentityProvider:Authority"] = "http://idp" }, null, CancellationToken.None));
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
