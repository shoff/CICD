using Cicd.Contracts;
using Cicd.Core.Agents;
using Cicd.Core.Builds;
using Cicd.Core.Entities;
using Cicd.Core.Services;
using Cicd.Server.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace Cicd.Server.Realtime;

public sealed class SignalRBuildEventPublisher(IHubContext<BuildHub> hub, BuildEventStream stream, AgentConnectionRegistry connections) : IBuildEventPublisher
{
    public async Task BuildUpdatedAsync(Build build, CancellationToken cancellationToken)
    {
        var dto = build.ToDto();
        stream.RaiseBuildUpdated(dto);
        await hub.Clients.All.SendAsync(BuildHubEvents.BuildUpdated, dto, cancellationToken);
    }

    public async Task BuildLogAsync(Guid buildId, IReadOnlyList<LogLine> lines, CancellationToken cancellationToken)
    {
        stream.RaiseBuildLog(buildId, lines);
        await hub.Clients.Group(BuildHub.BuildGroup(buildId)).SendAsync(BuildHubEvents.BuildLog, buildId, lines, cancellationToken);
    }

    public async Task AgentUpdatedAsync(Agent agent, CancellationToken cancellationToken)
    {
        var dto = agent.ToDto(connections.IsConnected(agent.Id));
        stream.RaiseAgentUpdated(dto);
        await hub.Clients.All.SendAsync(BuildHubEvents.AgentUpdated, dto, cancellationToken);
    }
}
