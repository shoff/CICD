using Cicd.Core.Persistence;
using Cicd.Core.Settings;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Cicd.Server.Tests;

/// <summary>In-memory SQLite database with the provider-neutral model. Disposing drops it.</summary>
public sealed class TestDb : IDisposable
{
    private readonly SqliteConnection connection = new("DataSource=:memory:");
    private readonly DbContextOptions<CicdDbContext> options;
    private readonly ISecretProtector protector;

    public TestDb(ISecretProtector? protector = null)
    {
        this.protector = protector ?? NullSecretProtector.Instance;
        connection.Open();
        options = new DbContextOptionsBuilder<CicdDbContext>()
            .UseSqlite(connection)
            .ReplaceService<IModelCacheKeyFactory, ProtectorModelCacheKeyFactory>()
            .Options;
        using var db = Create();
        db.Database.EnsureCreated();
    }

    public CicdDbContext Create() => new(options, protector);

    public void Dispose() => connection.Dispose();
}
