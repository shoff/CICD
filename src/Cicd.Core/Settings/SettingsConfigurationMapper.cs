using Cicd.Core.Entities;
using Microsoft.Extensions.Logging;

namespace Cicd.Core.Settings;

/// <summary>Turns settings rows into configuration keys: secrets decoded, lists expanded to indexed children.</summary>
public static class SettingsConfigurationMapper
{
    private static readonly Dictionary<string, IReadOnlyCollection<string>> NoShadowedLists = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Stands in for a secret that cannot be decrypted. Random per process, so no presented token or webhook
    /// signature can match it, and non-empty, so consumers do not read it as "not configured".
    /// </summary>
    public static string UnreadableSecret { get; } = "unreadable:" + Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));

    public static IDictionary<string, string?> ToConfiguration(IEnumerable<Setting> rows, ISecretProtector protector, ILogger? logger = null) =>
        ToConfiguration(rows, protector, NoShadowedLists, logger);

    /// <summary>
    /// <paramref name="shadowedListKeys"/> maps a list key to the child keys the lower configuration layers define for
    /// it. <see cref="Microsoft.Extensions.Configuration.ConfigurationRoot"/> unions the children of every provider,
    /// so a shrunk or cleared list would still show the entries appsettings or the environment contributed. Emitting an
    /// explicit null for each lower child this row does not fill hides the lower value: a provider that returns true
    /// from TryGet with a null value wins. Children are matched by key, not counted, because the lower layers need not
    /// be contiguous (<c>Security__BootstrapAdmins__5</c>). The binder turns such a child into a null element, so the
    /// options registration strips nulls (see <c>AddSecurityOptions</c>).
    /// </summary>
    public static IDictionary<string, string?> ToConfiguration(
        IEnumerable<Setting> rows, ISecretProtector protector, IReadOnlyDictionary<string, IReadOnlyCollection<string>> shadowedListKeys, ILogger? logger)
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
                // Fail closed. The key still shadows the appsettings or environment value, and it is not empty: an
                // empty Security:ApiToken means open mode and an empty GitHub:WebhookSecret skips signature checks,
                // so "unset" would switch protection off. A value nobody can present makes every comparison fail.
                logger?.LogError(ex, "Setting {Key} cannot be decrypted; nothing will match it until it is re-entered", row.Key);
                result[row.Key] = UnreadableSecret;
                continue;
            }
            if (definition is not null && !definition.Accepts(value))
            {
                // Last line of defence for a row written by hand or by an older build: one unbindable number or
                // boolean would make every read of its options section throw, including the page that fixes it.
                logger?.LogError("Setting {Key} holds a value that is not a valid {Kind}; using the default until it is corrected", row.Key, definition.Kind);
                result[row.Key] = definition.DefaultValue;
                continue;
            }
            if (definition?.Kind == SettingKind.List)
            {
                var items = SplitList(value);
                for (var i = 0; i < items.Count; i++)
                {
                    result[$"{row.Key}:{i}"] = items[i];
                }
                foreach (var childKey in shadowedListKeys.TryGetValue(row.Key, out var childKeys) ? childKeys : [])
                {
                    result.TryAdd($"{row.Key}:{childKey}", null);
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
