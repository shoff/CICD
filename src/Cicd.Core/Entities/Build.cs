using Cicd.Contracts;

namespace Cicd.Core.Entities;

public sealed class Build
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid BuildConfigurationId { get; set; }
    public BuildConfiguration? BuildConfiguration { get; set; }
    public required string Number { get; set; }
    public BuildStatus Status { get; set; } = BuildStatus.Queued;
    public string? StatusText { get; set; }
    public required string Branch { get; set; }
    public string? Revision { get; set; }
    /// <summary>Ref to fetch on the agent when it differs from the branch, e.g. refs/pull/12/head.</summary>
    public string? CheckoutRef { get; set; }
    public required string TriggeredBy { get; set; }
    public Guid? AgentId { get; set; }
    public Agent? Agent { get; set; }
    public Guid? PullRequestId { get; set; }
    public PullRequest? PullRequest { get; set; }
    public DateTimeOffset QueuedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public long LogLineCount { get; set; }
    public List<BuildStepRun> StepRuns { get; set; } = [];
    public bool CancelRequested { get; set; }
}

public sealed class BuildStepRun
{
    public int Index { get; set; }
    public required string Name { get; set; }
    public required string TypeId { get; set; }
    public StepStatus Status { get; set; } = StepStatus.Pending;
    public string? StatusText { get; set; }
}

public sealed class BuildLogLine
{
    public long Id { get; set; }
    public Guid BuildId { get; set; }
    public long Sequence { get; set; }
    public DateTimeOffset Timestamp { get; set; }
    public Contracts.LogLevel Level { get; set; }
    public int? StepIndex { get; set; }
    public required string Text { get; set; }
}

public sealed class BuildArtifact
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid BuildId { get; set; }
    public required string Path { get; set; }
    public long SizeBytes { get; set; }
    public required string StoragePath { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
