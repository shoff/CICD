using System.Reflection;
using Cicd.Plugins.Sdk;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Cicd.Core.Plugins;

public sealed class PluginHostOptions
{
    public const string SectionName = "Plugins";
    /// <summary>Root directory containing one sub-directory per plugin. See <see cref="PluginDirectoryLocator"/> for defaults.</summary>
    public string? Directory { get; set; }
    /// <summary>Plugin ids to skip even if present on disk.</summary>
    public List<string> Disabled { get; set; } = [];
}

public static class PluginHost
{
    /// <summary>
    /// Discovers plugins under the configured directory, instantiates each <see cref="IPlugin"/> and lets it register
    /// its contributions into <paramref name="services"/>. Call this before building the service provider.
    /// </summary>
    public static PluginCatalog LoadPlugins(this IServiceCollection services, IConfiguration configuration, PluginSide side, ILogger? logger = null)
    {
        var options = configuration.GetSection(PluginHostOptions.SectionName).Get<PluginHostOptions>() ?? new PluginHostOptions();
        var root = PluginDirectoryLocator.Resolve(options.Directory);
        var catalog = new PluginCatalog(side);

        if (root is null || !System.IO.Directory.Exists(root))
        {
            logger?.LogWarning("No plugin directory found (configured: '{Configured}'). Running without plugins.", options.Directory);
            services.AddSingleton(catalog);
            return catalog;
        }

        logger?.LogInformation("Loading {Side} plugins from {Directory}", side, root);
        foreach (var directory in System.IO.Directory.EnumerateDirectories(root).Order(StringComparer.Ordinal))
        {
            var manifestPath = Path.Combine(directory, PluginManifestReader.FileName);
            if (!File.Exists(manifestPath))
            {
                continue;
            }
            try
            {
                var loaded = LoadOne(directory, manifestPath, side, options, configuration, services);
                if (loaded is not null)
                {
                    catalog.Add(loaded);
                    logger?.LogInformation("Loaded plugin {PluginId} {Version} ({Contributions})", loaded.Manifest.Id, loaded.Manifest.Version, string.Join(", ", loaded.Contributions));
                }
            }
            catch (Exception ex)
            {
                var message = $"Failed to load plugin from '{directory}': {ex.GetBaseException().Message}";
                catalog.AddFailure(message);
                logger?.LogError(ex, "Failed to load plugin from {Directory}", directory);
            }
        }

        services.AddSingleton(catalog);
        return catalog;
    }

    private static LoadedPlugin? LoadOne(string directory, string manifestPath, PluginSide side, PluginHostOptions options, IConfiguration configuration, IServiceCollection services)
    {
        var manifest = PluginManifestReader.Read(manifestPath);
        if (options.Disabled.Contains(manifest.Id, StringComparer.OrdinalIgnoreCase))
        {
            return null;
        }
        if ((manifest.Sides & side) == PluginSide.None)
        {
            return null;
        }

        var assemblyPath = Path.Combine(directory, manifest.Assembly);
        if (!File.Exists(assemblyPath))
        {
            throw new FileNotFoundException($"Plugin '{manifest.Id}' names assembly '{manifest.Assembly}' which does not exist.", assemblyPath);
        }

        var context = new PluginLoadContext(assemblyPath);
        var assembly = context.LoadFromAssemblyName(AssemblyName.GetAssemblyName(assemblyPath));
        var pluginTypes = assembly.GetTypes()
            .Where(t => typeof(IPlugin).IsAssignableFrom(t) && t is { IsClass: true, IsAbstract: false })
            .ToList();
        if (pluginTypes.Count != 1)
        {
            throw new InvalidOperationException($"Plugin '{manifest.Id}' must contain exactly one IPlugin implementation, found {pluginTypes.Count}.");
        }

        var plugin = (IPlugin)Activator.CreateInstance(pluginTypes[0])!;
        var registrar = new PluginRegistrar(side, manifest, configuration, services);
        plugin.Configure(registrar);
        return new LoadedPlugin(manifest, directory, registrar.Contributions);
    }

    /// <summary>Registers in-process plugins without touching the file system. Used by tests and by hosts that bundle plugins.</summary>
    public static PluginCatalog AddInProcessPlugins(this IServiceCollection services, IConfiguration configuration, PluginSide side, params (PluginManifest Manifest, IPlugin Plugin)[] plugins)
    {
        var catalog = new PluginCatalog(side);
        foreach (var (manifest, plugin) in plugins)
        {
            var registrar = new PluginRegistrar(side, manifest, configuration, services);
            plugin.Configure(registrar);
            catalog.Add(new LoadedPlugin(manifest, "<in-process>", registrar.Contributions));
        }
        services.AddSingleton(catalog);
        return catalog;
    }
}
