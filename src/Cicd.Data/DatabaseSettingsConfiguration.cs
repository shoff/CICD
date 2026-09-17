using Cicd.Core.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Cicd.Data;

/// <summary>Configuration source over the settings table. Added last so stored values win over appsettings and environment.</summary>
public sealed class DatabaseSettingsConfigurationSource(
    string connectionString, ISecretProtector protector, IReadOnlyDictionary<string, int> shadowedListLengths, ILogger? logger = null) : IConfigurationSource
{
    public DatabaseSettingsConfigurationProvider Provider { get; } = new(connectionString, protector, shadowedListLengths, logger);

    public IConfigurationProvider Build(IConfigurationBuilder builder) => Provider;
}

/// <summary>
/// <paramref name="shadowedListLengths"/> tells the mapper how many indices each list key already has in the lower
/// layers, so a cleared or shrunk stored list hides them instead of being unioned with them.
/// </summary>
public sealed class DatabaseSettingsConfigurationProvider(
    string connectionString, ISecretProtector protector, IReadOnlyDictionary<string, int> shadowedListLengths, ILogger? logger = null)
    : ConfigurationProvider, ISettingsReloader
{
    private Dictionary<string, string?>? startup;

    /// <summary>Set once the host is built, so reload diagnostics reach the application log and not the disposed startup factory.</summary>
    public ILogger? Logger { get; set; } = logger;

    public IReadOnlyDictionary<string, string?> ValuesAtStartup => startup ?? new Dictionary<string, string?>();

    public override void Load()
    {
        var options = PostgresServiceCollectionExtensions.Configure(new DbContextOptionsBuilder<PostgresCicdDbContext>(), connectionString).Options;
        using var db = new PostgresCicdDbContext(options, protector);
        var rows = db.Settings.AsNoTracking().ToList();
        var mapped = SettingsConfigurationMapper.ToConfiguration(rows, protector, shadowedListLengths, Logger);
        Data = new Dictionary<string, string?>(mapped, StringComparer.OrdinalIgnoreCase);
        startup ??= new Dictionary<string, string?>(Data, StringComparer.OrdinalIgnoreCase);
    }

    public void Reload()
    {
        Load();
        OnReload();
    }
}
