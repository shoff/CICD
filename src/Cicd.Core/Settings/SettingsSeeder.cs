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
            var value = definition.Kind switch
            {
                SettingKind.List => SettingsConfigurationMapper.JoinList(configuration.GetSection(definition.Key).GetChildren().Select(c => c.Value ?? "")),
                // JSON booleans arrive as "True"/"False"; store the casing UpdateAsync writes so comparisons stay ordinal.
                SettingKind.Boolean when bool.TryParse(configuration[definition.Key], out var flag) => flag ? "true" : "false",
                // Nothing in configuration, or a number or boolean the binder cannot read (an empty environment
                // variable reads as "", not null): fall back to the catalog default.
                _ => configuration[definition.Key] is { } configured && definition.Accepts(configured)
                    ? configured
                    : definition.DefaultValue ?? "",
            };
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
