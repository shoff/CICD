using Cicd.Contracts;
using Cicd.Core.Entities;
using Cicd.Core.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Cicd.Core.Builds;

public sealed record QueueBuildCommand(
    Guid BuildConfigurationId,
    string? Branch = null,
    string? Revision = null,
    string? CheckoutRef = null,
    string TriggeredBy = "manual",
    Guid? PullRequestId = null);

public sealed class BuildQueueService(CicdDbContext db, BuildEventBroadcaster broadcaster, IAgentChannel agentChannel, TimeProvider clock, ILogger<BuildQueueService> logger)
{
    public async Task<Build> QueueAsync(QueueBuildCommand command, CancellationToken cancellationToken)
    {
        var configuration = await db.BuildConfigurations
            .Include(c => c.VcsRoot)
            .Include(c => c.Project)
            .FirstOrDefaultAsync(c => c.Id == command.BuildConfigurationId, cancellationToken)
            ?? throw new KeyNotFoundException($"Build configuration {command.BuildConfigurationId} not found.");

        var branch = command.Branch ?? configuration.VcsRoot?.DefaultBranch ?? "main";
        Build build;
        while (true)
        {
            configuration.BuildCounter++;
            build = new Build
            {
                BuildConfigurationId = configuration.Id,
                Number = BuildNumberFormatter.Format(configuration.BuildNumberFormat, configuration.BuildCounter, configuration.Parameters),
                Branch = branch,
                Revision = command.Revision,
                CheckoutRef = command.CheckoutRef,
                TriggeredBy = command.TriggeredBy,
                PullRequestId = command.PullRequestId,
                QueuedAt = clock.GetUtcNow(),
                StepRuns = configuration.Steps.Select((step, index) => new BuildStepRun { Index = index, Name = step.Name, TypeId = step.TypeId }).ToList(),
            };
            db.Builds.Add(build);
            try
            {
                await db.SaveChangesAsync(cancellationToken);
                break;
            }
            catch (DbUpdateConcurrencyException)
            {
                // Another request bumped the counter first. Reload and retry so build numbers stay unique.
                db.Builds.Remove(build);
                await db.Entry(configuration).ReloadAsync(cancellationToken);
            }
        }

        build.BuildConfiguration = configuration;
        logger.LogInformation("Queued build {Number} of {Configuration} on {Branch} ({TriggeredBy})", build.Number, configuration.Name, branch, command.TriggeredBy);
        await broadcaster.BuildUpdatedAsync(build, cancellationToken);
        return build;
    }

    public async Task<Build?> CancelAsync(Guid buildId, string requestedBy, CancellationToken cancellationToken)
    {
        var build = await db.Builds.Include(b => b.BuildConfiguration).FirstOrDefaultAsync(b => b.Id == buildId, cancellationToken);
        if (build is null || build.Status.IsFinished())
        {
            return build;
        }

        if (build.Status == BuildStatus.Queued)
        {
            build.Status = BuildStatus.Canceled;
            build.StatusText = $"Canceled by {requestedBy} while queued";
            build.FinishedAt = clock.GetUtcNow();
            await db.SaveChangesAsync(cancellationToken);
            await broadcaster.BuildUpdatedAsync(build, cancellationToken);
            return build;
        }

        build.CancelRequested = true;
        await db.SaveChangesAsync(cancellationToken);
        if (build.AgentId is { } agentId)
        {
            await agentChannel.CancelBuildAsync(agentId, build.Id, cancellationToken);
        }
        return build;
    }
}
