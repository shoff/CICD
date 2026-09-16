namespace Cicd.Core.Plugins;

public static class PluginDirectoryLocator
{
    /// <summary>
    /// Resolves the plugin root directory. Order: explicit configuration value, then a "plugins" folder next to the
    /// executable (Docker layout), then the repository's artifacts/plugins folder (development layout).
    /// </summary>
    public static string? Resolve(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return Path.GetFullPath(configured);
        }

        var local = Path.Combine(AppContext.BaseDirectory, "plugins");
        if (Directory.Exists(local))
        {
            return local;
        }

        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, "artifacts", "plugins");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
            current = current.Parent;
        }
        return null;
    }
}
