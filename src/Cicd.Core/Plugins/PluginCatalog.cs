using Cicd.Plugins.Sdk;

namespace Cicd.Core.Plugins;

public sealed record LoadedPlugin(PluginManifest Manifest, string Directory, IReadOnlyList<string> Contributions);

/// <summary>Read-only view of what was loaded at startup. Registered as a singleton.</summary>
public sealed class PluginCatalog
{
    private readonly List<LoadedPlugin> plugins = [];
    private readonly List<string> failures = [];

    public PluginSide Side { get; }

    public PluginCatalog(PluginSide side)
    {
        Side = side;
    }

    public IReadOnlyList<LoadedPlugin> Plugins => plugins;
    public IReadOnlyList<string> Failures => failures;

    internal void Add(LoadedPlugin plugin) => plugins.Add(plugin);
    internal void AddFailure(string message) => failures.Add(message);
}
