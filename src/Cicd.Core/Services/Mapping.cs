using Cicd.Contracts.Api;
using Cicd.Core.Entities;
using Cicd.Core.Plugins;
using Cicd.Plugins.Sdk;

namespace Cicd.Core.Services;

public static class Mapping
{
    public static ProjectDto ToDto(this Project p) => new(p.Id, p.Name, p.Description, p.ParentId, p.CreatedAt);

    public static VcsRootDto ToDto(this VcsRoot v) => new(v.Id, v.ProjectId, v.Name, v.ProviderId, v.Url, v.DefaultBranch, Redact(v.Properties));

    public static BuildConfigurationDto ToDto(this BuildConfiguration c) => new(
        c.Id, c.ProjectId, c.Name, c.Description, c.VcsRootId, c.BuildNumberFormat, c.BuildCounter,
        c.Steps.Select(s => new BuildStepDto(s.Name, s.TypeId, s.Parameters, s.ExecutionPolicy)).ToList(),
        c.Parameters,
        c.Triggers.Select(t => new TriggerDto(t.TypeId, t.Parameters)).ToList(),
        c.AgentRequirements,
        c.ArtifactPaths,
        new PullRequestFeatureDto(c.PullRequests.Enabled, c.PullRequests.TargetBranchFilter, c.PullRequests.ReportStatus));

    public static BuildDto ToDto(this Build b) => new(
        b.Id, b.BuildConfigurationId, b.BuildConfiguration?.Name ?? "", b.BuildConfiguration?.ProjectId ?? Guid.Empty,
        b.BuildConfiguration?.Project?.Name ?? "", b.Number, b.Status, b.StatusText, b.Branch, b.Revision, b.TriggeredBy,
        b.AgentId, b.Agent?.Name, b.PullRequestId, b.QueuedAt, b.StartedAt, b.FinishedAt);

    public static ArtifactDto ToDto(this BuildArtifact a) => new(a.Id, a.Path, a.SizeBytes, a.CreatedAt);

    public static AgentDto ToDto(this Agent a, bool connected) => new(a.Id, a.Name, a.Version, a.Authorized, a.Enabled, connected, a.CurrentBuildId, a.LastSeenAt, a.Capabilities);

    public static PullRequestDto ToDto(this PullRequest p) => new(p.Id, p.VcsRootId, p.Number, p.Title, p.SourceBranch, p.TargetBranch, p.HeadSha, p.Author, p.State, p.Url, p.UpdatedAt);

    public static UserDto ToDto(this User u) => new(u.Id, u.Username, u.Email, u.DisplayName, u.Role, u.Disabled, u.CreatedAt, u.LastSeenAt);

    public static PluginDto ToDto(this LoadedPlugin p) => new(
        p.Manifest.Id, p.Manifest.Name, p.Manifest.Version, p.Manifest.Description,
        SidesOf(p.Manifest.Sides), p.Contributions);

    public static StepTypeDto ToDto(this IBuildStepType t) => new(t.Id, t.DisplayName, t.Description,
        t.Parameters.Select(p => new ParameterDefinitionDto(p.Name, p.DisplayName, p.Description, p.Required, p.DefaultValue, p.Kind.ToString())).ToList());

    private static IReadOnlyList<string> SidesOf(PluginSide sides)
    {
        var list = new List<string>();
        if (sides.HasFlag(PluginSide.Server))
        {
            list.Add("server");
        }

        if (sides.HasFlag(PluginSide.Agent))
        {
            list.Add("agent");
        }

        return list;
    }

    private static readonly string[] SecretHints = ["token", "password", "secret", "key"];

    private static IReadOnlyDictionary<string, string> Redact(Dictionary<string, string> properties) =>
        properties.ToDictionary(kv => kv.Key, kv => SecretHints.Any(h => kv.Key.Contains(h, StringComparison.OrdinalIgnoreCase)) ? "********" : kv.Value);
}
