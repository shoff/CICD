using Cicd.Contracts;
using Cicd.Core.Entities;
using Cicd.Plugins.Sdk;
using Microsoft.Extensions.Logging;

namespace Cicd.Core.Builds;

public sealed class BuildJobFactory(IEnumerable<IVcsProvider> vcsProviders, ILogger<BuildJobFactory> logger)
{
    public async Task<BuildJob> CreateAsync(Build build, BuildConfiguration configuration, CancellationToken cancellationToken)
    {
        var project = configuration.Project ?? throw new InvalidOperationException("Configuration must have its project loaded.");
        var root = configuration.VcsRoot;

        var revision = build.Revision;
        if (revision is null && root is not null)
        {
            var provider = vcsProviders.FirstOrDefault(p => string.Equals(p.Id, root.ProviderId, StringComparison.OrdinalIgnoreCase));
            if (provider is not null)
            {
                var current = await provider.GetCurrentRevisionAsync(root.ToInfo(), build.Branch, cancellationToken);
                revision = current?.Revision;
            }
        }

        var parameters = new Dictionary<string, string>(configuration.Parameters, StringComparer.Ordinal)
        {
            ["build.id"] = build.Id.ToString(),
            ["build.number"] = build.Number,
            ["build.counter"] = configuration.BuildCounter.ToString(),
            ["build.branch"] = build.Branch,
            ["build.revision"] = revision ?? "",
            ["build.triggered.by"] = build.TriggeredBy,
            ["project.name"] = project.Name,
            ["configuration.name"] = configuration.Name,
            ["configuration.id"] = configuration.Id.ToString(),
        };
        if (root is not null)
        {
            parameters["vcs.url"] = root.Url;
            parameters["vcs.branch"] = build.Branch;
            parameters["vcs.revision"] = revision ?? "";
        }
        var resolved = ParameterResolver.ResolveAll(parameters);

        var steps = configuration.Steps.Select((step, index) => new BuildStepDefinition
        {
            Index = index,
            Name = step.Name,
            TypeId = step.TypeId,
            ExecutionPolicy = step.ExecutionPolicy,
            Parameters = step.Parameters.ToDictionary(kv => kv.Key, kv => ParameterResolver.Resolve(kv.Value, resolved)),
        }).ToList();

        if (root is not null && root.Properties.Count == 0)
        {
            // Either none were set, or the stored credentials could not be decrypted (see ProtectedJson.DecodeProperties).
            logger.LogWarning("VCS root {Name} has no readable credentials", root.Name);
        }

        return new BuildJob
        {
            BuildId = build.Id,
            BuildConfigurationId = configuration.Id,
            BuildConfigurationName = configuration.Name,
            ProjectName = project.Name,
            BuildNumber = build.Number,
            Checkout = root is null ? null : new VcsCheckoutSpec
            {
                ProviderId = root.ProviderId,
                Url = root.Url,
                Branch = build.CheckoutRef ?? build.Branch,
                Revision = revision,
                Properties = root.Properties,
            },
            Steps = steps,
            Parameters = resolved,
            ArtifactPaths = configuration.ArtifactPaths,
        };
    }
}

public static class VcsRootExtensions
{
    public static VcsRootInfo ToInfo(this VcsRoot root) => new(root.Id, root.ProviderId, root.Url, root.DefaultBranch, root.Properties);
}
