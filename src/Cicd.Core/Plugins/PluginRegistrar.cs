using Cicd.Plugins.Sdk;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Cicd.Core.Plugins;

internal sealed class PluginRegistrar(PluginSide side, PluginManifest manifest, IConfiguration configuration, IServiceCollection services) : IPluginRegistrar
{
    private readonly List<string> contributions = [];

    public PluginSide Side { get; } = side;
    public PluginManifest Manifest { get; } = manifest;
    public IConfiguration Configuration { get; } = configuration;
    public IServiceCollection Services { get; } = services;
    public IReadOnlyList<string> Contributions => contributions;

    public IPluginRegistrar AddStepType<T>() where T : class, IBuildStepType => Register<IBuildStepType, T>(PluginSide.Server, "step-type");
    public IPluginRegistrar AddRunner<T>() where T : class, IBuildRunner => Register<IBuildRunner, T>(PluginSide.Agent, "runner");
    public IPluginRegistrar AddVcsProvider<T>() where T : class, IVcsProvider => Register<IVcsProvider, T>(PluginSide.Server, "vcs-provider");
    public IPluginRegistrar AddVcsCheckout<T>() where T : class, IVcsCheckout => Register<IVcsCheckout, T>(PluginSide.Agent, "vcs-checkout");
    public IPluginRegistrar AddPullRequestProvider<T>() where T : class, IPullRequestProvider => Register<IPullRequestProvider, T>(PluginSide.Server, "pull-request-provider");
    public IPluginRegistrar AddTrigger<T>() where T : class, IBuildTrigger => Register<IBuildTrigger, T>(PluginSide.Server, "trigger");
    public IPluginRegistrar AddNotifier<T>() where T : class, INotifier => Register<INotifier, T>(PluginSide.Server, "notifier");
    public IPluginRegistrar AddWebhookHandler<T>() where T : class, IWebhookHandler => Register<IWebhookHandler, T>(PluginSide.Server, "webhook-handler");
    public IPluginRegistrar AddCapabilityProvider<T>() where T : class, IAgentCapabilityProvider => Register<IAgentCapabilityProvider, T>(PluginSide.Agent, "capability-provider");

    private PluginRegistrar Register<TService, TImplementation>(PluginSide applicableSide, string kind)
        where TService : class
        where TImplementation : class, TService
    {
        if ((Side & applicableSide) == PluginSide.None)
        {
            return this;
        }
        Services.AddSingleton<TService, TImplementation>();
        contributions.Add($"{kind}:{typeof(TImplementation).Name}");
        return this;
    }
}
