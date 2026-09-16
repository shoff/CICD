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
