namespace Cicd.Contracts.Api;

public sealed record ProjectDto(Guid Id, string Name, string? Description, Guid? ParentId, DateTimeOffset CreatedAt);

public sealed record CreateProjectRequest(string Name, string? Description = null, Guid? ParentId = null);

public sealed record VcsRootDto(
    Guid Id,
    Guid ProjectId,
    string Name,
    string ProviderId,
    string Url,
    string DefaultBranch,
    IReadOnlyDictionary<string, string> Properties);

public sealed record CreateVcsRootRequest(
    Guid ProjectId,
    string Name,
    string ProviderId,
    string Url,
    string DefaultBranch = "main",
    IReadOnlyDictionary<string, string>? Properties = null);

public sealed record BuildStepDto(
    string Name,
    string TypeId,
    IReadOnlyDictionary<string, string> Parameters,
    StepExecutionPolicy ExecutionPolicy = StepExecutionPolicy.Default);

public sealed record TriggerDto(string TypeId, IReadOnlyDictionary<string, string> Parameters);

public sealed record PullRequestFeatureDto(bool Enabled, string TargetBranchFilter = "*", bool ReportStatus = true);

public sealed record BuildConfigurationDto(
    Guid Id,
    Guid ProjectId,
    string Name,
    string? Description,
    Guid? VcsRootId,
    string BuildNumberFormat,
    int BuildCounter,
    IReadOnlyList<BuildStepDto> Steps,
    IReadOnlyDictionary<string, string> Parameters,
    IReadOnlyList<TriggerDto> Triggers,
    IReadOnlyDictionary<string, string> AgentRequirements,
    IReadOnlyList<string> ArtifactPaths,
    PullRequestFeatureDto PullRequests);

public sealed record UpsertBuildConfigurationRequest(
    Guid ProjectId,
    string Name,
    string? Description = null,
    Guid? VcsRootId = null,
    string BuildNumberFormat = "%build.counter%",
    IReadOnlyList<BuildStepDto>? Steps = null,
    IReadOnlyDictionary<string, string>? Parameters = null,
    IReadOnlyList<TriggerDto>? Triggers = null,
    IReadOnlyDictionary<string, string>? AgentRequirements = null,
    IReadOnlyList<string>? ArtifactPaths = null,
    PullRequestFeatureDto? PullRequests = null);

public sealed record QueueBuildRequest(string? Branch = null, string? Revision = null, string? Comment = null);

public sealed record BuildDto(
    Guid Id,
    Guid BuildConfigurationId,
    string BuildConfigurationName,
    Guid ProjectId,
    string ProjectName,
    string Number,
    BuildStatus Status,
    string? StatusText,
    string Branch,
    string? Revision,
    string TriggeredBy,
    Guid? AgentId,
    string? AgentName,
    Guid? PullRequestId,
    DateTimeOffset QueuedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt);

public sealed record BuildLogDto(Guid BuildId, IReadOnlyList<LogLine> Lines, long LastSequence, bool Finished);

public sealed record ArtifactDto(Guid Id, string Path, long SizeBytes, DateTimeOffset CreatedAt);

public sealed record AgentDto(
    Guid Id,
    string Name,
    string Version,
    bool Authorized,
    bool Enabled,
    bool Connected,
    Guid? CurrentBuildId,
    DateTimeOffset? LastSeenAt,
    IReadOnlyDictionary<string, string> Capabilities);

public sealed record PullRequestDto(
    Guid Id,
    Guid VcsRootId,
    long Number,
    string Title,
    string SourceBranch,
    string TargetBranch,
    string HeadSha,
    string Author,
    string State,
    string? Url,
    DateTimeOffset UpdatedAt);

public sealed record PluginDto(string Id, string Name, string Version, string Description, IReadOnlyList<string> Sides, IReadOnlyList<string> Contributions);

public sealed record StepTypeDto(string Id, string DisplayName, string Description, IReadOnlyList<ParameterDefinitionDto> Parameters);

public sealed record ParameterDefinitionDto(string Name, string DisplayName, string? Description, bool Required, string? DefaultValue, string Kind);

public sealed record UserDto(
    Guid Id,
    string? Username,
    string? Email,
    string? DisplayName,
    UserRole Role,
    bool Disabled,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastSeenAt);

public sealed record SetUserRoleRequest(UserRole Role);

public sealed record SetUserDisabledRequest(bool Disabled);
