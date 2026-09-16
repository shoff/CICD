using Cicd.Contracts;
using Cicd.Contracts.Api;

namespace Cicd.Server.Realtime;

/// <summary>In-process fan-out used by Blazor components so the UI does not need a second SignalR hop.</summary>
public sealed class BuildEventStream
{
    public event Action<BuildDto>? BuildUpdated;
    public event Action<Guid, IReadOnlyList<LogLine>>? BuildLog;
    public event Action<AgentDto>? AgentUpdated;

    internal void RaiseBuildUpdated(BuildDto build) => BuildUpdated?.Invoke(build);
    internal void RaiseBuildLog(Guid buildId, IReadOnlyList<LogLine> lines) => BuildLog?.Invoke(buildId, lines);
    internal void RaiseAgentUpdated(AgentDto agent) => AgentUpdated?.Invoke(agent);
}
