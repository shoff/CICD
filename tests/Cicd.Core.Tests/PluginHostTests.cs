using Cicd.Core.Plugins;
using Cicd.Plugins.CommandLine;
using Cicd.Plugins.Sdk;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Cicd.Core.Tests;

public class PluginHostTests
{
    private static readonly IConfiguration EmptyConfiguration = new ConfigurationBuilder().Build();

    /// <summary>What a generic host registers before plugins load: configuration and logging.</summary>
    private static ServiceCollection HostLikeServices(IConfiguration configuration)
    {
        var services = new ServiceCollection();
        services.AddSingleton(configuration);
        services.AddLogging();
        return services;
    }

    [Fact]
    public void Manifest_parses_sides_as_array_or_string()
    {
        var array = PluginManifestReader.Parse("""{ "id": "x", "name": "X", "version": "1.0", "assembly": "X.dll", "sides": ["server", "agent"] }""");
        Assert.Equal(PluginSide.Both, array.Sides);
        var single = PluginManifestReader.Parse("""{ "id": "x", "name": "X", "version": "1.0", "assembly": "X.dll", "sides": "agent" }""");
        Assert.Equal(PluginSide.Agent, single.Sides);
        var missing = PluginManifestReader.Parse("""{ "id": "x", "name": "X", "version": "1.0", "assembly": "X.dll" }""");
        Assert.Equal(PluginSide.Both, missing.Sides);
    }

    [Fact]
    public void Manifest_rejects_bad_ids()
    {
        Assert.Throws<InvalidDataException>(() => PluginManifestReader.Parse("""{ "id": "bad id!", "name": "X", "version": "1.0", "assembly": "X.dll" }"""));
    }

    [Fact]
    public void Registrar_filters_contributions_by_side()
    {
        var manifest = new PluginManifest { Id = "command-line", Name = "Command Line", Version = "1.0", Assembly = "Cicd.Plugins.CommandLine.dll" };

        var serverServices = new ServiceCollection();
        var serverCatalog = serverServices.AddInProcessPlugins(EmptyConfiguration, PluginSide.Server, (manifest, new CommandLinePlugin()));
        using var server = serverServices.BuildServiceProvider();
        Assert.Single(server.GetServices<IBuildStepType>());
        Assert.Empty(server.GetServices<IBuildRunner>());
        Assert.Contains("step-type:CommandLineStepType", serverCatalog.Plugins[0].Contributions);

        var agentServices = new ServiceCollection();
        agentServices.AddInProcessPlugins(EmptyConfiguration, PluginSide.Agent, (manifest, new CommandLinePlugin()));
        using var agent = agentServices.BuildServiceProvider();
        Assert.Empty(agent.GetServices<IBuildStepType>());
        Assert.Single(agent.GetServices<IBuildRunner>());
    }

    [Fact]
    public void Loads_built_plugins_from_artifacts_directory_in_isolation()
    {
        var root = PluginDirectoryLocator.Resolve(null);
        Assert.False(root is null, "artifacts/plugins was not found; build the plugin projects first.");

        var services = HostLikeServices(EmptyConfiguration);
        var catalog = services.LoadPlugins(EmptyConfiguration, PluginSide.Server);
        Assert.Empty(catalog.Failures);
        Assert.Contains(catalog.Plugins, p => p.Manifest.Id == "command-line");
        Assert.Contains(catalog.Plugins, p => p.Manifest.Id == "git");
        Assert.Contains(catalog.Plugins, p => p.Manifest.Id == "github");

        using var provider = services.BuildServiceProvider();
        var stepTypes = provider.GetServices<IBuildStepType>().Select(t => t.Id).ToList();
        Assert.Contains("command-line", stepTypes);
        Assert.Contains("dotnet", stepTypes);
        Assert.Contains(provider.GetServices<IVcsProvider>(), p => p.Id == "git");
        Assert.Contains(provider.GetServices<IPullRequestProvider>(), p => p.Id == "github");
        // Server side never gets runners.
        Assert.Empty(provider.GetServices<IBuildRunner>());
    }

    [Fact]
    public void Disabled_plugins_are_skipped()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Plugins:Disabled:0"] = "github" }).Build();
        var services = HostLikeServices(configuration);
        var catalog = services.LoadPlugins(configuration, PluginSide.Server);
        Assert.DoesNotContain(catalog.Plugins, p => p.Manifest.Id == "github");
        Assert.Contains(catalog.Plugins, p => p.Manifest.Id == "git");
    }
}
