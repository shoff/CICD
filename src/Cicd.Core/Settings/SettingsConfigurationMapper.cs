using Cicd.Core.Entities;
using Microsoft.Extensions.Logging;

namespace Cicd.Core.Settings;

/// <summary>Turns settings rows into configuration keys: secrets decoded, lists expanded to indexed children.</summary>
public static class SettingsConfigurationMapper
{
    public static IDictionary<string, string?> ToConfiguration(IEnumerable<Setting> rows, ISecretProtector protector, ILogger? logger = null)
    {
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            var definition = SettingsCatalog.Find(row.Key);
            string value;
            try
            {
                value = row.IsSecret ? SecretCodec.Decode(protector, row.Value) : row.Value;
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "Setting {Key} cannot be decrypted; it is ignored until re-entered", row.Key);
                continue;
            }
            if (definition?.Kind == SettingKind.List)
            {
                var items = SplitList(value);
                for (var i = 0; i < items.Count; i++)
                {
                    result[$"{row.Key}:{i}"] = items[i];
                }
                continue;
            }
            result[row.Key] = value;
        }
        return result;
    }

    public static string JoinList(IEnumerable<string> items) => string.Join(',', items.Select(i => i.Trim()).Where(i => i.Length > 0));

    public static IReadOnlyList<string> SplitList(string? value) =>
        string.IsNullOrWhiteSpace(value) ? [] : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
