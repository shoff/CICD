using Cicd.Contracts;

namespace Cicd.Core.Entities;

public sealed class BuildConfiguration
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public Project? Project { get; set; }
    public required string Name { get; set; }
    public string? Description { get; set; }
    public Guid? VcsRootId { get; set; }
    public VcsRoot? VcsRoot { get; set; }
    /// <summary>TeamCity-style format, e.g. "1.0.%build.counter%".</summary>
    public string BuildNumberFormat { get; set; } = "%build.counter%";
    public int BuildCounter { get; set; }
    public List<BuildStep> Steps { get; set; } = [];
    public Dictionary<string, string> Parameters { get; set; } = new();
    public List<TriggerSetting> Triggers { get; set; } = [];
    /// <summary>Capability name to required value. A value of "*" means "present with any value".</summary>
    public Dictionary<string, string> AgentRequirements { get; set; } = new();
    /// <summary>Glob patterns relative to the checkout directory, published by the agent after the build.</summary>
    public List<string> ArtifactPaths { get; set; } = [];
    public PullRequestFeature PullRequests { get; set; } = new();
    public bool Paused { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class BuildStep
{
    public required string Name { get; set; }
    public required string TypeId { get; set; }
    public Dictionary<string, string> Parameters { get; set; } = new();
    public StepExecutionPolicy ExecutionPolicy { get; set; } = StepExecutionPolicy.Default;
}

public sealed class TriggerSetting
{
    public required string TypeId { get; set; }
    public Dictionary<string, string> Parameters { get; set; } = new();
}

public sealed class PullRequestFeature
{
    public bool Enabled { get; set; }
    /// <summary>Glob on the PR target branch, "*" for all.</summary>
    public string TargetBranchFilter { get; set; } = "*";
    /// <summary>Publish build status back to the hosting service.</summary>
    public bool ReportStatus { get; set; } = true;
}
