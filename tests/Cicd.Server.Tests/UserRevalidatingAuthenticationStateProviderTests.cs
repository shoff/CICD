using System.Security.Claims;
using Cicd.Contracts;
using Cicd.Core.Entities;
using Cicd.Core.Persistence;
using Cicd.Core.Users;
using Cicd.Server.Security;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Cicd.Server.Tests;

public class UserRevalidatingAuthenticationStateProviderTests : IDisposable
{
    private readonly TestDb testDb = new();
    private readonly TestClock clock = new();
    private readonly ServiceProvider services;

    public UserRevalidatingAuthenticationStateProviderTests()
    {
        var collection = new ServiceCollection();
        collection.AddScoped(_ => testDb.Create());
        collection.AddScoped<UserService>();
        collection.AddSingleton<TimeProvider>(clock);
        collection.AddSingleton(Options.Create(new SecurityOptions()));
        services = collection.BuildServiceProvider();
    }

    /// <summary>Exposes the protected revalidation hook the circuit timer would otherwise call.</summary>
    private sealed class Probe(IServiceScopeFactory scopes) : UserRevalidatingAuthenticationStateProvider(scopes, NullLoggerFactory.Instance)
    {
        public Task<bool> ValidateAsync(ClaimsPrincipal principal) =>
            ValidateAuthenticationStateAsync(new AuthenticationState(principal), CancellationToken.None);
    }

    private Probe Provider() => new(services.GetRequiredService<IServiceScopeFactory>());

    private static ClaimsPrincipal Principal(Guid? userId, string? role)
    {
        var claims = new List<Claim>();
        if (userId is not null)
        {
            claims.Add(new Claim(LocalUserClaimsTransformation.UserIdClaim, userId.Value.ToString()));
        }
        if (role is not null)
        {
            claims.Add(new Claim(ClaimTypes.Role, role));
        }
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Test", ClaimTypes.Name, ClaimTypes.Role));
    }

    private async Task<Guid> AddUserAsync(UserRole role, bool disabled)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CicdDbContext>();
        var user = new User { Issuer = "https://idp", Subject = "sub-1", Role = role, Disabled = disabled };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    [Fact]
    public async Task Principal_without_a_user_id_claim_is_left_alone()
    {
        using var provider = Provider();
        Assert.True(await provider.ValidateAsync(Principal(null, Roles.Admin)));
    }

    [Fact]
    public async Task Active_user_whose_claim_matches_the_row_stays_valid()
    {
        var id = await AddUserAsync(UserRole.Viewer, disabled: false);
        using var provider = Provider();
        Assert.True(await provider.ValidateAsync(Principal(id, Roles.Viewer)));
    }

    [Fact]
    public async Task Disabled_user_is_revoked()
    {
        var id = await AddUserAsync(UserRole.Viewer, disabled: true);
        using var provider = Provider();
        Assert.False(await provider.ValidateAsync(Principal(id, Roles.Viewer)));
    }

    [Fact]
    public async Task Role_change_since_sign_in_is_revoked()
    {
        var id = await AddUserAsync(UserRole.Admin, disabled: false);
        using var provider = Provider();
        Assert.False(await provider.ValidateAsync(Principal(id, Roles.Viewer)));
    }

    [Fact]
    public async Task Missing_user_row_is_revoked()
    {
        using var provider = Provider();
        Assert.False(await provider.ValidateAsync(Principal(Guid.NewGuid(), Roles.Viewer)));
    }

    public void Dispose()
    {
        services.Dispose();
        testDb.Dispose();
    }
}
