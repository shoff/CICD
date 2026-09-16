using Cicd.Contracts;
using Cicd.Core.Builds;
using Cicd.Core.Entities;
using Cicd.Core.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Cicd.Core.Agents;

public sealed class AgentService(CicdDbContext db, AgentConnectionRegistry connections, BuildEventBroadcaster broadcaster, BuildProgressService progress, TimeProvider clock, ILogger<AgentService> logger)
{
    public async Task<AgentRegistrationResult> RegisterAsync(AgentRegistration registration, string connectionId, bool autoAuthorize, CancellationToken cancellationToken)
    {
        var agent = await db.Agents.FirstOrDefaultAsync(a => a.Name == registration.Name, cancellationToken);
        if (agent is null)
        {
            agent = new Agent { Name = registration.Name, Authorized = autoAuthorize };
            db.Agents.Add(agent);
            logger.LogInformation("New agent {Agent} registered (authorized: {Authorized})", registration.Name, autoAuthorize);
        }
        agent.Version = registration.Version;
        agent.Capabilities = new Dictionary<string, string>(registration.Capabilities);
        agent.LastSeenAt = clock.GetUtcNow();
        if (agent.CurrentBuildId is not null)
        {
            // A reconnecting agent has lost its in-flight build; the agent process restarted.
            await progress.AgentLostAsync(agent.Id, cancellationToken);
            agent.CurrentBuildId = null;
        }
        await db.SaveChangesAsync(cancellationToken);
        connections.Bind(agent.Id, connectionId);
        await broadcaster.AgentUpdatedAsync(agent, cancellationToken);
        return new AgentRegistrationResult { AgentId = agent.Id, Authorized = agent.Authorized, Enabled = agent.Enabled };
    }

    public async Task HeartbeatAsync(Guid agentId, CancellationToken cancellationToken)
    {
        await db.Agents.Where(a => a.Id == agentId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(a => a.LastSeenAt, clock.GetUtcNow()), cancellationToken);
    }

    public async Task DisconnectedAsync(string connectionId, CancellationToken cancellationToken)
    {
        var agentId = connections.Unbind(connectionId);
        if (agentId is null)
        {
            return;
        }
        var agent = await db.Agents.FirstOrDefaultAsync(a => a.Id == agentId, cancellationToken);
        if (agent is null)
        {
            return;
        }
        logger.LogWarning("Agent {Agent} disconnected", agent.Name);
        if (agent.CurrentBuildId is not null)
        {
            await progress.AgentLostAsync(agent.Id, cancellationToken);
            agent.CurrentBuildId = null;
            await db.SaveChangesAsync(cancellationToken);
        }
        await broadcaster.AgentUpdatedAsync(agent, cancellationToken);
    }

    public async Task<Agent?> SetAuthorizedAsync(Guid agentId, bool authorized, CancellationToken cancellationToken)
    {
        var agent = await db.Agents.FirstOrDefaultAsync(a => a.Id == agentId, cancellationToken);
        if (agent is null) return null;
        agent.Authorized = authorized;
        await db.SaveChangesAsync(cancellationToken);
        await broadcaster.AgentUpdatedAsync(agent, cancellationToken);
        return agent;
    }

    public async Task<Agent?> SetEnabledAsync(Guid agentId, bool enabled, CancellationToken cancellationToken)
    {
        var agent = await db.Agents.FirstOrDefaultAsync(a => a.Id == agentId, cancellationToken);
        if (agent is null) return null;
        agent.Enabled = enabled;
        await db.SaveChangesAsync(cancellationToken);
        await broadcaster.AgentUpdatedAsync(agent, cancellationToken);
        return agent;
    }
}
