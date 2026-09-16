using Cicd.Contracts;
using Cicd.Core.Agents;
using Cicd.Core.Builds;
using Cicd.Server.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

namespace Cicd.Server.Hubs;

/// <summary>Agent-facing hub. Agents authenticate with the shared agent token.</summary>
[Authorize(Policy = Policies.Agent)]
public sealed class AgentHub(AgentService agents, BuildProgressService progress, AgentConnectionRegistry connections, IOptions<AgentsOptions> options, ILogger<AgentHub> logger) : Hub
{
    public Task<AgentRegistrationResult> Register(AgentRegistration registration) =>
        agents.RegisterAsync(registration, Context.ConnectionId, options.Value.AutoAuthorize, Context.ConnectionAborted);

    public Task Heartbeat()
    {
        var agentId = RequireAgent();
        return agents.HeartbeatAsync(agentId, Context.ConnectionAborted);
    }

    public Task BuildStarted(Guid buildId) => progress.BuildStartedAsync(RequireAgent(), buildId, Context.ConnectionAborted);

    public Task Log(Guid buildId, LogLine[] lines)
    {
        RequireAgent();
        return progress.AppendLogAsync(buildId, lines, Context.ConnectionAborted);
    }

    public Task StepChanged(StepProgress step)
    {
        RequireAgent();
        return progress.StepChangedAsync(step, Context.ConnectionAborted);
    }

    public Task BuildFinished(BuildResult result) => progress.BuildFinishedAsync(RequireAgent(), result, Context.ConnectionAborted);

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        if (exception is not null)
        {
            logger.LogDebug(exception, "Agent connection {ConnectionId} dropped", Context.ConnectionId);
        }
        await agents.DisconnectedAsync(Context.ConnectionId, CancellationToken.None);
        await base.OnDisconnectedAsync(exception);
    }

    private Guid RequireAgent() =>
        connections.GetAgentId(Context.ConnectionId) ?? throw new HubException("Agent must call Register before other methods.");
}
