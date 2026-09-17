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
