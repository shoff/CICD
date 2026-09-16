namespace Cicd.Contracts;

/// <summary>
/// Everything an agent needs to execute one build. Produced by the server, consumed by the agent.
/// </summary>
public sealed record BuildJob
{
    public required Guid BuildId { get; init; }
    public required Guid BuildConfigurationId { get; init; }
    public required string BuildConfigurationName { get; init; }
    public required string ProjectName { get; init; }
    public required string BuildNumber { get; init; }
    public VcsCheckoutSpec? Checkout { get; init; }
    public required IReadOnlyList<BuildStepDefinition> Steps { get; init; }
    /// <summary>Resolved configuration parameters, already substituted. Exposed to steps as environment variables.</summary>
    public required IReadOnlyDictionary<string, string> Parameters { get; init; }
    public IReadOnlyList<string> ArtifactPaths { get; init; } = [];
}

public sealed record VcsCheckoutSpec
{
    public required string ProviderId { get; init; }
    public required string Url { get; init; }
    public required string Branch { get; init; }
    public string? Revision { get; init; }
    public IReadOnlyDictionary<string, string> Properties { get; init; } = new Dictionary<string, string>();
}

public sealed record BuildStepDefinition
{
    public required int Index { get; init; }
    public required string Name { get; init; }
    /// <summary>The step type id, e.g. "command-line" or "dotnet". Resolved to a runner plugin on the agent.</summary>
    public required string TypeId { get; init; }
    public required IReadOnlyDictionary<string, string> Parameters { get; init; }
    public StepExecutionPolicy ExecutionPolicy { get; init; } = StepExecutionPolicy.Default;
}

public enum StepExecutionPolicy
{
    /// <summary>Run only if all previous steps succeeded.</summary>
    Default = 0,
    /// <summary>Run even if previous steps failed.</summary>
    Always = 1,
    /// <summary>Run only if a previous step failed.</summary>
    OnlyIfPreviousFailed = 2,
}
