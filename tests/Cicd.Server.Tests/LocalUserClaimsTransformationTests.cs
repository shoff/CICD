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
        IdpPrincipal([], scheme, subject);

    private static ClaimsPrincipal IdpPrincipal(Claim[] extra, string scheme = "Bearer", string subject = "sub-1") =>
        new(new ClaimsIdentity(
            [
                new Claim("iss", "https://idp"),
                new Claim("sub", subject),
                new Claim("email", "alice@example.com"),
                new Claim("name", "Alice"),
                .. extra,
            ],
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
        var principal = IdpPrincipal();
        var first = await transformation.TransformAsync(principal);
        var second = await transformation.TransformAsync(principal);
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

    [Fact]
    public async Task Inbound_role_claims_from_the_idp_are_ignored()
    {
        var (transformation, _, _) = Build();
        var principal = IdpPrincipal([new Claim(ClaimTypes.Role, "admin"), new Claim(ClaimTypes.Role, "agent")]);
        var result = await transformation.TransformAsync(principal);
        Assert.True(result.IsInRole("viewer"));
        Assert.False(result.IsInRole("admin"));
        Assert.False(result.IsInRole("agent"));
    }

    [Fact]
    public async Task Inbound_cicd_claims_from_the_idp_are_replaced()
    {
        var (transformation, db, _) = Build();
        var principal = IdpPrincipal(
        [
            new Claim(LocalUserClaimsTransformation.UserIdClaim, "00000000-0000-0000-0000-000000000001"),
            new Claim(LocalUserClaimsTransformation.DisabledClaim, "true"),
        ]);
        var result = await transformation.TransformAsync(principal);
        var user = await db.Users.SingleAsync();
        var userIds = result.FindAll(LocalUserClaimsTransformation.UserIdClaim).ToList();
        Assert.Equal(user.Id.ToString(), Assert.Single(userIds).Value);
        Assert.Empty(result.FindAll(LocalUserClaimsTransformation.DisabledClaim));
        Assert.True(result.IsInRole("viewer"));
    }

    public void Dispose() => testDb.Dispose();
}
