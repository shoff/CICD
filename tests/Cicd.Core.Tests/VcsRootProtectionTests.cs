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
    public async Task Undecryptable_properties_load_as_empty_instead_of_throwing()
    {
        using var testDb = new TestDb(new ThrowingProtector());
        await using (var db = testDb.Create())
        {
            var project = new Project { Name = "p" };
            db.Projects.Add(project);
            await db.SaveChangesAsync();
            await db.Database.ExecuteSqlRawAsync(
                "insert into vcs_roots (Id, ProjectId, Name, ProviderId, Url, DefaultBranch, Properties, CreatedAt) values ({0}, {1}, 'lost-keys', 'git', 'https://example.com/l.git', 'main', {2}, {3})",
                Guid.NewGuid(), project.Id, "enc:v1:garbage", DateTimeOffset.UtcNow);
        }
        await using (var db = testDb.Create())
        {
            var root = await db.VcsRoots.SingleAsync(r => r.Name == "lost-keys");
            Assert.Empty(root.Properties);
        }
    }

    [Fact]
    public async Task Startup_check_names_only_the_roots_whose_credentials_cannot_be_decrypted()
    {
        using var testDb = new TestDb(new PickyProtector());
        await using var db = testDb.Create();
        var project = new Project { Name = "p" };
        db.Projects.Add(project);
        db.VcsRoots.Add(Root(project.Id));
        await db.SaveChangesAsync();
        const string insert = "insert into vcs_roots (Id, ProjectId, Name, ProviderId, Url, DefaultBranch, Properties, CreatedAt) values ({0}, {1}, {2}, 'git', 'https://example.com/l.git', 'main', {3}, {4})";
        await db.Database.ExecuteSqlRawAsync(insert, Guid.NewGuid(), project.Id, "lost-keys", "enc:v1:garbage", DateTimeOffset.UtcNow);
        await db.Database.ExecuteSqlRawAsync(insert, Guid.NewGuid(), project.Id, "legacy", """{"git.password":"old"}""", DateTimeOffset.UtcNow);

        var unreadable = await Cicd.Core.Persistence.ProtectedJson.UnreadableVcsRootsAsync(db, new PickyProtector(), CancellationToken.None);

        Assert.Equal(["lost-keys"], unreadable);
    }

    /// <summary>Reads what it wrote and nothing else, like a key ring that has lost an older key.</summary>
    private sealed class PickyProtector : Cicd.Core.Settings.ISecretProtector
    {
        public string Protect(string plaintext) => "ok:" + plaintext;
        public string Unprotect(string ciphertext) =>
            ciphertext.StartsWith("ok:", StringComparison.Ordinal) ? ciphertext[3..] : throw new InvalidOperationException("no key");
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
                "insert into vcs_roots (Id, ProjectId, Name, ProviderId, Url, DefaultBranch, Properties, CreatedAt) values ({0}, {1}, 'legacy', 'git', 'https://example.com/l.git', 'main', {2}, {3})",
                Guid.NewGuid(), project.Id, """{"git.password":"old"}""", DateTimeOffset.UtcNow);
        }
        await using (var db = testDb.Create())
        {
            var root = await db.VcsRoots.SingleAsync(r => r.Name == "legacy");
            Assert.Equal("old", root.Properties["git.password"]);
        }
    }
}
