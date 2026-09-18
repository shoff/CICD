using Cicd.Core.Entities;
using Cicd.Core.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Cicd.Core.Settings;

/// <summary>Lets the settings service push new values into the live configuration and see what the host started with.</summary>
public interface ISettingsReloader
{
    IReadOnlyDictionary<string, string?> ValuesAtStartup { get; }
    void Reload();
}

public sealed record SettingView(SettingDefinition Definition, string Value, bool IsSet, bool RestartPending, bool Unreadable);

/// <summary>A rejected update: nothing was written. Separated from the reload failure so the API can answer 400, not 409.</summary>
public sealed class SettingsValidationException(string message) : InvalidOperationException(message);

/// <summary>
/// The values were stored but the running configuration could not be reloaded. Its own type so callers do not report
/// every <see cref="InvalidOperationException"/> - EF throws those too - as "saved".
/// </summary>
public sealed class SettingsReloadException(string message, Exception inner) : InvalidOperationException(message, inner);

public sealed class SettingsService(CicdDbContext db, ISecretProtector protector, ISettingsReloader reloader, TimeProvider clock, ILogger<SettingsService> logger)
{
    public const string Mask = "********";

    public async Task<IReadOnlyList<SettingView>> GetAllAsync(CancellationToken cancellationToken)
    {
        var rows = await db.Settings.AsNoTracking().ToDictionaryAsync(s => s.Key, StringComparer.OrdinalIgnoreCase, cancellationToken);
        var views = new List<SettingView>();
        foreach (var definition in SettingsCatalog.All)
        {
            rows.TryGetValue(definition.Key, out var row);
            var stored = row?.Value ?? "";
            var unreadable = false;
            var value = stored;
            if (definition.Kind == SettingKind.Secret)
            {
                try
                {
                    value = SecretCodec.Decode(protector, stored);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Setting {Key} cannot be decrypted", definition.Key);
                    unreadable = true;
                    value = "";
                }
            }
            var isSet = value.Length > 0;
            // Booleans are compared case-insensitively: configuration renders JSON booleans as "True"/"False".
            var comparison = definition.Kind == SettingKind.Boolean ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            var restartPending = definition.RestartRequired
                && !string.Equals(value, StartupValue(definition), comparison);
            var shown = definition.Kind == SettingKind.Secret ? (isSet ? Mask : "") : value;
            views.Add(new SettingView(definition, shown, isSet, restartPending, unreadable));
        }
        return views;
    }

    /// <summary>
    /// What the host started with, in the same shape as the stored row. Lists live in configuration as indexed
    /// children (<c>Plugins:Disabled:0</c>), so they are joined back into the stored comma-separated form.
    /// </summary>
    private string StartupValue(SettingDefinition definition)
    {
        if (definition.Kind != SettingKind.List)
        {
            return reloader.ValuesAtStartup.GetValueOrDefault(definition.Key) ?? "";
        }
        var prefix = definition.Key + ":";
        var items = reloader.ValuesAtStartup
            .Where(pair => pair.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .OrderBy(pair => int.TryParse(pair.Key[prefix.Length..], out var index) ? index : int.MaxValue)
            .Select(pair => pair.Value ?? "");
        return SettingsConfigurationMapper.JoinList(items);
    }

    /// <summary>
    /// Validates everything first; on any error nothing is written and a <see cref="SettingsValidationException"/> is
    /// thrown. For a secret, an empty string means "unchanged" and null means "clear it".
    /// </summary>
    public async Task UpdateAsync(IReadOnlyDictionary<string, string?> values, string? updatedBy, CancellationToken cancellationToken)
    {
        var errors = new List<string>(Validate(values));
        errors.AddRange(await CrossFieldErrorsAsync(values, cancellationToken));
        if (errors.Count > 0)
        {
            throw new SettingsValidationException(string.Join(" ", errors));
        }
        var now = clock.GetUtcNow();
        foreach (var (key, raw) in values)
        {
            var definition = SettingsCatalog.Find(key)!;
            if (definition.Kind == SettingKind.Secret && raw is not null && raw.Length == 0)
            {
                continue;
            }
            var value = raw ?? "";
            var row = await db.Settings.FirstOrDefaultAsync(s => s.Key == definition.Key, cancellationToken);
            if (row is null)
            {
                row = new Setting { Key = definition.Key };
                db.Settings.Add(row);
            }
            row.Value = definition.Kind switch
            {
                // A cleared secret stores an empty value, which the configuration mapper passes on as unset.
                SettingKind.Secret => value.Length == 0 ? "" : SecretCodec.Encode(protector, value),
                SettingKind.List => SettingsConfigurationMapper.JoinList(SettingsConfigurationMapper.SplitList(value)),
                SettingKind.Boolean => bool.Parse(value).ToString().ToLowerInvariant(),
                _ => value.Trim(),
            };
            row.IsSecret = definition.Kind == SettingKind.Secret;
            row.UpdatedAt = now;
            row.UpdatedBy = updatedBy;
        }
        await db.SaveChangesAsync(cancellationToken);
        try
        {
            reloader.Reload();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Settings were saved but the running configuration could not be reloaded");
            throw new SettingsReloadException("Saved, but the running configuration could not be reloaded. Check the server log.", ex);
        }
    }

    /// <summary>
    /// Rules that need more than one key. They run only when the payload touches the IdentityProvider section: a save
    /// of some other section must never be blocked by identity data it does not carry and cannot fix. Within the
    /// section the submitted values are merged over what is stored, because a section can be saved a field at a time,
    /// so the check sees the state the save would produce and not just what it carries.
    /// </summary>
    private async Task<IReadOnlyList<string>> CrossFieldErrorsAsync(IReadOnlyDictionary<string, string?> values, CancellationToken cancellationToken)
    {
        const string section = IdentityProviderSection;
        if (!values.Keys.Any(key => SettingsCatalog.Find(key)?.Section == IdentityProviderSectionName))
        {
            return [];
        }
        var merged = await db.Settings.AsNoTracking()
            .Where(s => s.Key.StartsWith(section))
            .ToDictionaryAsync(s => s.Key, s => s.Value, StringComparer.OrdinalIgnoreCase, cancellationToken);
        foreach (var (key, raw) in values)
        {
            if (key.StartsWith(section, StringComparison.OrdinalIgnoreCase))
            {
                merged[key] = (raw ?? "").Trim();
            }
        }

        var errors = new List<string>();
        if (IsTrue(merged, "IdentityProvider:ValidateAudience") && Value(merged, "IdentityProvider:Audience").Length == 0)
        {
            errors.Add($"{Name("IdentityProvider:Audience")} is required when {Name("IdentityProvider:ValidateAudience")} is on.");
        }
        if (IsTrue(merged, "IdentityProvider:RequireHttpsMetadata"))
        {
            foreach (var key in HttpsUrlKeys)
            {
                var url = Value(merged, key);
                if (url.Length > 0 && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    errors.Add($"{Name(key)} must start with https:// while {Name("IdentityProvider:RequireHttpsMetadata")} is on.");
                }
            }
        }
        return errors;
    }

    private const string IdentityProviderSectionName = "IdentityProvider";
    private const string IdentityProviderSection = IdentityProviderSectionName + ":";

    /// <summary>Provider URLs that must be https while <c>IdentityProvider:RequireHttpsMetadata</c> is on.</summary>
    private static readonly string[] HttpsUrlKeys = ["IdentityProvider:Authority", "IdentityProvider:LoginUrl"];

    private static string Value(IReadOnlyDictionary<string, string> merged, string key) =>
        merged.TryGetValue(key, out var value) ? value : "";

    private static bool IsTrue(IReadOnlyDictionary<string, string> merged, string key) =>
        bool.TryParse(Value(merged, key), out var flag) && flag;

    private static string Name(string key) => SettingsCatalog.Find(key)?.DisplayName ?? key;

    /// <summary>
    /// Per-key checks. A null value is accepted: for a secret it means "clear it", and for any other kind it is
    /// treated as an empty string.
    /// </summary>
    public static IReadOnlyList<string> Validate(IReadOnlyDictionary<string, string?> values)
    {
        var errors = new List<string>();
        foreach (var (key, raw) in values)
        {
            var definition = SettingsCatalog.Find(key);
            if (definition is null)
            {
                errors.Add($"'{key}' is not a managed setting.");
                continue;
            }
            var value = (raw ?? "").Trim();
            if (definition.Accepts(value))
            {
                continue;
            }
            // An empty number is rejected like any other unreadable one: stored, it would break the binder.
            errors.Add(definition.Kind == SettingKind.Number
                ? $"{definition.DisplayName} must be a non-negative whole number."
                : $"{definition.DisplayName} must be true or false.");
        }
        return errors;
    }
}
