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

    /// <summary>Validates everything first; on any error nothing is written. Empty secrets mean "unchanged".</summary>
    public async Task UpdateAsync(IReadOnlyDictionary<string, string?> values, string? updatedBy, CancellationToken cancellationToken)
    {
        var errors = Validate(values);
        if (errors.Count > 0)
        {
            throw new InvalidOperationException(string.Join(" ", errors));
        }
        var now = clock.GetUtcNow();
        foreach (var (key, raw) in values)
        {
            var definition = SettingsCatalog.Find(key)!;
            var value = raw ?? "";
            if (definition.Kind == SettingKind.Secret && value.Length == 0)
            {
                continue;
            }
            var row = await db.Settings.FirstOrDefaultAsync(s => s.Key == definition.Key, cancellationToken);
            if (row is null)
            {
                row = new Setting { Key = definition.Key };
                db.Settings.Add(row);
            }
            row.Value = definition.Kind switch
            {
                SettingKind.Secret => SecretCodec.Encode(protector, value),
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
            throw new InvalidOperationException("Saved, but the running configuration could not be reloaded. Check the server log.", ex);
        }
    }

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
            switch (definition.Kind)
            {
                case SettingKind.Number when value.Length > 0 && (!int.TryParse(value, out var number) || number < 0):
                    errors.Add($"{definition.DisplayName} must be a non-negative whole number.");
                    break;
                case SettingKind.Boolean when !bool.TryParse(value, out _):
                    errors.Add($"{definition.DisplayName} must be true or false.");
                    break;
            }
        }
        return errors;
    }
}
