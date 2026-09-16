using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Cicd.Plugins.Sdk;

/// <summary>
/// Which process a plugin (or one of its contributions) runs in. Mirrors TeamCity's server-side / agent-side split.
/// </summary>
[Flags]
public enum PluginSide
{
    None = 0,
    Server = 1,
    Agent = 2,
    Both = Server | Agent,
}

/// <summary>
/// Entry point of a plugin. Exactly one public, non-abstract implementation must exist per plugin assembly.
/// The host discovers it, instantiates it with a parameterless constructor, and calls <see cref="Configure"/>
/// once during startup so the plugin can register its contributions.
/// </summary>
public interface IPlugin
{
    void Configure(IPluginRegistrar registrar);
}

/// <summary>
/// Given to a plugin at startup. Registrations are filtered by <see cref="Side"/>: a runner registered while
/// loading on the server is ignored, a step type descriptor registered while loading on the agent is ignored.
/// </summary>
public interface IPluginRegistrar
{
    PluginSide Side { get; }
    PluginManifest Manifest { get; }
    IConfiguration Configuration { get; }
    IServiceCollection Services { get; }

    IPluginRegistrar AddStepType<T>() where T : class, IBuildStepType;
    IPluginRegistrar AddRunner<T>() where T : class, IBuildRunner;
    IPluginRegistrar AddVcsProvider<T>() where T : class, IVcsProvider;
    IPluginRegistrar AddVcsCheckout<T>() where T : class, IVcsCheckout;
    IPluginRegistrar AddPullRequestProvider<T>() where T : class, IPullRequestProvider;
    IPluginRegistrar AddTrigger<T>() where T : class, IBuildTrigger;
    IPluginRegistrar AddNotifier<T>() where T : class, INotifier;
    IPluginRegistrar AddWebhookHandler<T>() where T : class, IWebhookHandler;
    IPluginRegistrar AddCapabilityProvider<T>() where T : class, IAgentCapabilityProvider;
}

/// <summary>Deserialized from a plugin directory's plugin.json.</summary>
public sealed record PluginManifest
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Version { get; init; }
    public string Description { get; init; } = "";
    /// <summary>File name of the assembly containing the <see cref="IPlugin"/> implementation.</summary>
    public required string Assembly { get; init; }
    public PluginSide Sides { get; init; } = PluginSide.Both;
}
