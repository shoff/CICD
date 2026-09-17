using Cicd.Core.Persistence;
using Cicd.Core.Settings;
using Microsoft.EntityFrameworkCore;

namespace Cicd.Data;

/// <summary>Adds PostgreSQL specifics (jsonb columns) on top of the provider-neutral model.</summary>
public sealed class PostgresCicdDbContext(DbContextOptions<PostgresCicdDbContext> options, ISecretProtector protector) : CicdDbContext(options, protector)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        foreach (var entity in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var property in entity.GetProperties())
            {
                if (property.FindAnnotation("Cicd:Json")?.Value is true)
                {
                    property.SetColumnType("jsonb");
                }
            }
        }
    }
}
