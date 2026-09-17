using Cicd.Core.Persistence;
using Cicd.Core.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Cicd.Data;

/// <summary>Used by `dotnet ef` when adding migrations. No database connection is needed to scaffold a migration.</summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<PostgresCicdDbContext>
{
    public PostgresCicdDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("CICD_CONNECTION_STRING")
            ?? "Host=localhost;Port=5432;Database=cicd;Username=cicd;Password=cicd";
        var options = PostgresServiceCollectionExtensions.Configure(new DbContextOptionsBuilder<PostgresCicdDbContext>(), connectionString);
        return new PostgresCicdDbContext(options.Options, NullSecretProtector.Instance);
    }
}
