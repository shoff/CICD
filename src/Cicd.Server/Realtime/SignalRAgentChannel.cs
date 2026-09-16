using Cicd.Contracts;
using Cicd.Core.Agents;
using Cicd.Core.Builds;
using Cicd.Server.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace Cicd.Server.Realtime;

public sealed class SignalRAgentChannel(IHubContext<AgentHub> hub, AgentConnectionRegistry connections) : IAgentChannel
{
    public Task RunBuildAsync(Guid agentId, BuildJob job, CancellationToken cancellationToken)
    {
        var connectionId = connections.GetConnectionId(agentId) ?? throw new InvalidOperationException($"Agent {agentId} is not connected.");
        // InvokeAsync waits for the agent to accept; a busy agent throws and the dispatcher requeues the build.
        return hub.Clients.Client(connectionId).InvokeAsync<bool>(AgentHubMethods.RunBuild, job, cancellationToken);
    }

    public Task CancelBuildAsync(Guid agentId, Guid buildId, CancellationToken cancellationToken)
    {
        var connectionId = connections.GetConnectionId(agentId);
        return connectionId is null ? Task.CompletedTask : hub.Clients.Client(connectionId).SendAsync(AgentHubMethods.CancelBuild, buildId, cancellationToken);
    }
}
