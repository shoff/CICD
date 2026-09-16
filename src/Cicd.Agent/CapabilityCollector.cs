using System.Runtime.InteropServices;
using Cicd.Core.Builds;
using Cicd.Plugins.Sdk;
using Microsoft.Extensions.Options;

namespace Cicd.Agent;

public sealed class CapabilityCollector(
    IEnumerable<IBuildRunner> runners,
    IEnumerable<IVcsCheckout> checkouts,
    IEnumerable<IAgentCapabilityProvider> providers,
    IOptions<AgentOptions> options,
    ILogger<CapabilityCollector> logger)
{
    public static string AgentVersion { get; } = typeof(CapabilityCollector).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    public async Task<Dictionary<string, string>> CollectAsync(CancellationToken cancellationToken)
    {
        var capabilities = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["agent.name"] = options.Value.Name,
            ["agent.version"] = AgentVersion,
            ["os"] = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux",
            ["os.description"] = RuntimeInformation.OSDescription,
            ["os.arch"] = RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant(),
            ["cpu.count"] = Environment.ProcessorCount.ToString(),
            ["container"] = File.Exists("/.dockerenv") ? "true" : "false",
        };
        foreach (var runner in runners)
        {
            capabilities[AgentMatcher.RunnerCapabilityPrefix + runner.TypeId] = "true";
        }
        foreach (var checkout in checkouts)
        {
            capabilities[AgentMatcher.VcsCapabilityPrefix + checkout.ProviderId] = "true";
        }
        foreach (var provider in providers)
        {
            try
            {
                foreach (var (key, value) in await provider.GetCapabilitiesAsync(cancellationToken))
                {
                    capabilities[key] = value;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Capability provider {Provider} failed", provider.GetType().Name);
            }
        }
        foreach (var (key, value) in options.Value.Capabilities)
        {
            capabilities[key] = value;
        }
        return capabilities;
    }
}
