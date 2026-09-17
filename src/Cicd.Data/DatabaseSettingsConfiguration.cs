using Cicd.Core.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Cicd.Data;

/// <summary>Configuration source over the settings table. Added last so stored values win over appsettings and environment.</summary>
public sealed class DatabaseSettingsConfigurationSource(string connectionString, ISecretProtector protector, ILogger? logger = null) : IConfigurationSource
{
    public DatabaseSettingsConfigurationProvider Provider { get; } = new(connectionString, protector, logger);

    public IConfigurationProvider Build(IConfigurationBuilder builder) => Provider;
}

public sealed class DatabaseSettingsConfigurationProvider(string connectionString, ISecretProtector protector, ILogger? logger = null) : ConfigurationProvider, ISettingsReloader
{
    private Dictionary<string, string?>? startup;

    public IReadOnlyDictionary<string, string?> ValuesAtStartup => startup ?? new Dictionary<string, string?>();

    public override void Load()
    {
        var options = PostgresServiceCollectionExtensions.Configure(new DbContextOptionsBuilder<PostgresCicdDbContext>(), connectionString).Options;
        using var db = new PostgresCicdDbContext(options, protector);
        var rows = db.Settings.AsNoTracking().ToList();
        var mapped = SettingsConfigurationMapper.ToConfiguration(rows, protector, logger);
        Data = new Dictionary<string, string?>(mapped, StringComparer.OrdinalIgnoreCase);
        startup ??= new Dictionary<string, string?>(Data, StringComparer.OrdinalIgnoreCase);
    }

    public void Reload()
    {
        Load();
        OnReload();
    }
}
