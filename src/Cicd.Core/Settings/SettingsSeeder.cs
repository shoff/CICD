using Cicd.Core.Entities;
using Microsoft.Extensions.Configuration;

namespace Cicd.Core.Settings;

/// <summary>First-run seeding: every cataloged key without a row gets one from the current configuration (appsettings, environment).</summary>
public static class SettingsSeeder
{
    public const string SeedAuthor = "seed";

    public static IReadOnlyList<Setting> MissingRows(IConfiguration configuration, IEnumerable<string> existingKeys, ISecretProtector protector, TimeProvider clock)
    {
        var existing = new HashSet<string>(existingKeys, StringComparer.OrdinalIgnoreCase);
        var now = clock.GetUtcNow();
        var rows = new List<Setting>();
        foreach (var definition in SettingsCatalog.All)
        {
            if (existing.Contains(definition.Key))
            {
                continue;
            }
            var value = definition.Kind == SettingKind.List
                ? SettingsConfigurationMapper.JoinList(configuration.GetSection(definition.Key).GetChildren().Select(c => c.Value ?? ""))
                : configuration[definition.Key] ?? "";
            var isSecret = definition.Kind == SettingKind.Secret;
            rows.Add(new Setting
            {
                Key = definition.Key,
                Value = isSecret && value.Length > 0 ? SecretCodec.Encode(protector, value) : value,
                IsSecret = isSecret,
                UpdatedAt = now,
                UpdatedBy = SeedAuthor,
            });
        }
        return rows;
    }
}
