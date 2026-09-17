using Cicd.Contracts;
using Cicd.Core.Agents;
using Cicd.Core.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cicd.Core.Builds;

/// <summary>
/// Assigns queued builds to compatible, idle, connected agents. Oldest build first, one build per agent at a time.
/// </summary>
public sealed class BuildDispatcher(IServiceScopeFactory scopeFactory, AgentConnectionRegistry connections, IAgentChannel channel, IOptionsMonitor<CicdServerOptions> options, TimeProvider clock, ILogger<BuildDispatcher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var interval = TimeSpan.FromSeconds(Math.Max(1, options.CurrentValue.DispatchIntervalSeconds));
            await Task.Delay(interval, stoppingToken);
            try
            {
                await DispatchOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Dispatch cycle failed");
            }
        }
    }

    public async Task<int> DispatchOnceAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CicdDbContext>();
        var jobFactory = scope.ServiceProvider.GetRequiredService<BuildJobFactory>();
        var broadcaster = scope.ServiceProvider.GetRequiredService<BuildEventBroadcaster>();

        var queued = await db.Builds
            .Include(b => b.BuildConfiguration!).ThenInclude(c => c.Project)
            .Include(b => b.BuildConfiguration!).ThenInclude(c => c.VcsRoot)
            .Where(b => b.Status == BuildStatus.Queued)
            .OrderBy(b => b.QueuedAt)
            .ToListAsync(cancellationToken);
        if (queued.Count == 0)
        {
            return 0;
        }

        var connectedIds = connections.ConnectedAgentIds;
        var agents = await db.Agents
            .Where(a => connectedIds.Contains(a.Id) && a.Authorized && a.Enabled && a.CurrentBuildId == null)
            .ToListAsync(cancellationToken);

        var dispatched = 0;
        foreach (var build in queued)
        {
            if (agents.Count == 0)
            {
                break;
            }
            var configuration = build.BuildConfiguration!;
            if (configuration.Paused)
            {
                continue;
            }
            var requirements = AgentMatcher.EffectiveRequirements(configuration);
            var agent = agents.FirstOrDefault(a => AgentMatcher.Matches(requirements, a.Capabilities));
            if (agent is null)
            {
                continue;
            }

            BuildJob job;
            try
            {
                job = await jobFactory.CreateAsync(build, configuration, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                build.Status = BuildStatus.Error;
                build.StatusText = $"Failed to prepare build: {ex.GetBaseException().Message}";
                build.FinishedAt = clock.GetUtcNow();
                await db.SaveChangesAsync(cancellationToken);
                await broadcaster.BuildUpdatedAsync(build, cancellationToken);
                continue;
            }

            build.Revision = job.Checkout?.Revision ?? build.Revision;
            build.AgentId = agent.Id;
            build.Status = BuildStatus.Running;
            build.StartedAt = clock.GetUtcNow();
            build.StatusText = $"Dispatched to {agent.Name}";
            agent.CurrentBuildId = build.Id;
            await db.SaveChangesAsync(cancellationToken);

            try
            {
                await channel.RunBuildAsync(agent.Id, job, cancellationToken);
                agents.Remove(agent);
                dispatched++;
                logger.LogInformation("Dispatched build {Number} of {Configuration} to agent {Agent}", build.Number, configuration.Name, agent.Name);
                await broadcaster.BuildUpdatedAsync(build, cancellationToken);
                await broadcaster.AgentUpdatedAsync(agent, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Agent {Agent} rejected build {Number}; returning it to the queue", agent.Name, build.Number);
                build.AgentId = null;
                build.Status = BuildStatus.Queued;
                build.StartedAt = null;
                build.StatusText = null;
                agent.CurrentBuildId = null;
                agents.Remove(agent);
                await db.SaveChangesAsync(cancellationToken);
            }
        }
        return dispatched;
    }
}
