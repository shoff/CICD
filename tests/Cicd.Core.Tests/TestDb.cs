using Cicd.Core.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Cicd.Core.Tests;

/// <summary>In-memory SQLite database with the provider-neutral model. Disposing drops it.</summary>
public sealed class TestDb : IDisposable
{
    private readonly SqliteConnection connection = new("DataSource=:memory:");
    private readonly DbContextOptions<CicdDbContext> options;

    public TestDb()
    {
        connection.Open();
        options = new DbContextOptionsBuilder<CicdDbContext>().UseSqlite(connection).Options;
        using var db = Create();
        db.Database.EnsureCreated();
    }

    public CicdDbContext Create() => new(options);

    public void Dispose() => connection.Dispose();
}
