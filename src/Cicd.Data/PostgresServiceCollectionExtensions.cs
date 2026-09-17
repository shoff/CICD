using Cicd.Core.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Cicd.Data;

public static class PostgresServiceCollectionExtensions
{
    public const string MigrationsAssembly = "Cicd.Data";

    /// <summary>
    /// Registers the PostgreSQL-backed context both as a scoped <see cref="CicdDbContext"/> (for request and hub scopes)
    /// and behind <see cref="IDbContextFactory{TContext}"/> (for long-lived Blazor circuits and background work).
    /// </summary>
    public static IServiceCollection AddCicdPostgres(this IServiceCollection services, string connectionString)
    {
        services.AddDbContextFactory<PostgresCicdDbContext>(options => Configure(options, connectionString));
        services.AddScoped<CicdDbContext>(sp => sp.GetRequiredService<PostgresCicdDbContext>());
        services.AddSingleton<IDbContextFactory<CicdDbContext>, ContextFactoryAdapter>();
        return services;
    }

    public static DbContextOptionsBuilder<PostgresCicdDbContext> Configure(DbContextOptionsBuilder<PostgresCicdDbContext> options, string connectionString)
    {
        Configure((DbContextOptionsBuilder)options, connectionString);
        return options;
    }

    private static void Configure(DbContextOptionsBuilder options, string connectionString)
    {
        options.UseNpgsql(connectionString, npgsql =>
        {
            npgsql.MigrationsAssembly(MigrationsAssembly);
            npgsql.EnableRetryOnFailure(maxRetryCount: 5);
        });
        options.UseSnakeCaseNamingConvention();
        options.ReplaceService<IModelCacheKeyFactory, ProtectorModelCacheKeyFactory>();
    }

    private sealed class ContextFactoryAdapter(IDbContextFactory<PostgresCicdDbContext> inner) : IDbContextFactory<CicdDbContext>
    {
        public CicdDbContext CreateDbContext() => inner.CreateDbContext();
        public async Task<CicdDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => await inner.CreateDbContextAsync(cancellationToken);
    }
}
