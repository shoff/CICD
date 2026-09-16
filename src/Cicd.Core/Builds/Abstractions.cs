using Cicd.Contracts;
using Cicd.Core.Entities;

namespace Cicd.Core.Builds;

/// <summary>Transport to a connected agent. Implemented in the server with SignalR.</summary>
public interface IAgentChannel
{
    Task RunBuildAsync(Guid agentId, BuildJob job, CancellationToken cancellationToken);
    Task CancelBuildAsync(Guid agentId, Guid buildId, CancellationToken cancellationToken);
}

/// <summary>Fan-out of build state to UI clients. Implemented in the server with SignalR.</summary>
public interface IBuildEventPublisher
{
    Task BuildUpdatedAsync(Build build, CancellationToken cancellationToken);
    Task BuildLogAsync(Guid buildId, IReadOnlyList<LogLine> lines, CancellationToken cancellationToken);
    Task AgentUpdatedAsync(Agent agent, CancellationToken cancellationToken);
}

public sealed class NullBuildEventPublisher : IBuildEventPublisher
{
    public Task BuildUpdatedAsync(Build build, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task BuildLogAsync(Guid buildId, IReadOnlyList<LogLine> lines, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task AgentUpdatedAsync(Agent agent, CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed class CicdServerOptions
{
    public const string SectionName = "Server";
    /// <summary>Externally reachable base URL, used in commit status links.</summary>
    public string PublicUrl { get; set; } = "http://localhost:8080";
    /// <summary>Root for artifacts and other server-side files.</summary>
    public string DataDirectory { get; set; } = "data";
    public int DispatchIntervalSeconds { get; set; } = 2;
    public int TriggerPollIntervalSeconds { get; set; } = 30;
    public int PullRequestPollIntervalSeconds { get; set; } = 60;
    /// <summary>Apply pending EF Core migrations on startup.</summary>
    public bool MigrateOnStartup { get; set; } = true;
}
