namespace Cicd.Contracts;

public sealed record AgentRegistration
{
    public required string Name { get; init; }
    public required string Version { get; init; }
    public required IReadOnlyDictionary<string, string> Capabilities { get; init; }
}

public sealed record AgentRegistrationResult
{
    public required Guid AgentId { get; init; }
    public required bool Authorized { get; init; }
    public required bool Enabled { get; init; }
}

public sealed record LogLine
{
    public required long Sequence { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
    public required LogLevel Level { get; init; }
    public int? StepIndex { get; init; }
    public required string Text { get; init; }
}

public sealed record StepProgress
{
    public required Guid BuildId { get; init; }
    public required int StepIndex { get; init; }
    public required StepStatus Status { get; init; }
    public string? StatusText { get; init; }
}

public sealed record BuildResult
{
    public required Guid BuildId { get; init; }
    public required BuildStatus Status { get; init; }
    public string? StatusText { get; init; }
    public IReadOnlyList<StepProgress> Steps { get; init; } = [];
}

/// <summary>Methods the server invokes on a connected agent.</summary>
public static class AgentHubMethods
{
    public const string RunBuild = nameof(RunBuild);
    public const string CancelBuild = nameof(CancelBuild);
    public const string Shutdown = nameof(Shutdown);
}

/// <summary>Methods an agent invokes on the server hub.</summary>
public static class ServerHubMethods
{
    public const string Register = nameof(Register);
    public const string Heartbeat = nameof(Heartbeat);
    public const string BuildStarted = nameof(BuildStarted);
    public const string Log = nameof(Log);
    public const string StepChanged = nameof(StepChanged);
    public const string BuildFinished = nameof(BuildFinished);
}

/// <summary>Events the server pushes to UI clients on the build hub.</summary>
public static class BuildHubEvents
{
    public const string BuildUpdated = nameof(BuildUpdated);
    public const string BuildLog = nameof(BuildLog);
    public const string AgentUpdated = nameof(AgentUpdated);
}
