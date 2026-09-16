using System.Security.Claims;
using Cicd.Contracts;
using Cicd.Core.Users;
using Cicd.Server.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;

namespace Cicd.Server.Tests;

public class RoleRequirementHandlerTests
{
    private static RoleRequirementHandler Handler(bool configured, string apiToken = "") =>
        new(Options.Create(new IdentityProviderOptions { Authority = configured ? "https://idp" : "" }),
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
        Assert.Equal(expected, await Passes(Handler(configured: true), minimum, UserWith(role)));

    [Fact]
    public async Task Anonymous_and_roleless_users_fail_when_configured()
    {
        var handler = Handler(configured: true);
        Assert.False(await Passes(handler, UserRole.Viewer, Anonymous));
        Assert.False(await Passes(handler, UserRole.Viewer, UserWith()));
    }

    [Fact]
    public async Task Open_mode_admits_anyone() =>
        Assert.True(await Passes(Handler(configured: false), UserRole.Admin, Anonymous));

    [Fact]
    public async Task Api_token_alone_closes_open_mode()
    {
        var handler = Handler(configured: false, apiToken: "secret");
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
