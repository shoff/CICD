using Cicd.Contracts;
using Cicd.Core.Entities;
using Cicd.Core.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Cicd.Core.Builds;

/// <summary>Applies agent-reported progress to the database and fans it out. One instance per hub invocation scope.</summary>
public sealed class BuildProgressService(CicdDbContext db, BuildEventBroadcaster broadcaster, TimeProvider clock, ILogger<BuildProgressService> logger)
{
    public async Task BuildStartedAsync(Guid agentId, Guid buildId, CancellationToken cancellationToken)
    {
        var build = await LoadAsync(buildId, cancellationToken);
        if (build is null || build.Status.IsFinished())
        {
            return;
        }
        build.Status = BuildStatus.Running;
        build.StartedAt ??= clock.GetUtcNow();
        build.AgentId ??= agentId;
        await db.SaveChangesAsync(cancellationToken);
        await broadcaster.BuildUpdatedAsync(build, cancellationToken);
    }

    public async Task AppendLogAsync(Guid buildId, IReadOnlyList<LogLine> lines, CancellationToken cancellationToken)
    {
        if (lines.Count == 0)
        {
            return;
        }
        var build = await db.Builds.FirstOrDefaultAsync(b => b.Id == buildId, cancellationToken);
        if (build is null)
        {
            return;
        }
        db.BuildLogLines.AddRange(lines.Select(line => new BuildLogLine
        {
            BuildId = buildId,
            Sequence = line.Sequence,
            Timestamp = line.Timestamp,
            Level = line.Level,
            StepIndex = line.StepIndex,
            Text = line.Text,
        }));
        build.LogLineCount = Math.Max(build.LogLineCount, lines[^1].Sequence + 1);
        await db.SaveChangesAsync(cancellationToken);
        await broadcaster.BuildLogAsync(buildId, lines, cancellationToken);
    }

    public async Task StepChangedAsync(StepProgress progress, CancellationToken cancellationToken)
    {
        var build = await LoadAsync(progress.BuildId, cancellationToken);
        if (build is null)
        {
            return;
        }
        var run = build.StepRuns.FirstOrDefault(s => s.Index == progress.StepIndex);
        if (run is null)
        {
            return;
        }
        run.Status = progress.Status;
        run.StatusText = progress.StatusText;
        build.StepRuns = [.. build.StepRuns]; // force change detection on the JSON column
        await db.SaveChangesAsync(cancellationToken);
        await broadcaster.BuildUpdatedAsync(build, cancellationToken);
    }

    public async Task BuildFinishedAsync(Guid agentId, BuildResult result, CancellationToken cancellationToken)
    {
        var build = await LoadAsync(result.BuildId, cancellationToken);
        if (build is null)
        {
            return;
        }
        if (!build.Status.IsFinished())
        {
            build.Status = result.Status;
            build.StatusText = result.StatusText;
            build.FinishedAt = clock.GetUtcNow();
            foreach (var step in result.Steps)
            {
                var run = build.StepRuns.FirstOrDefault(s => s.Index == step.StepIndex);
                if (run is not null)
                {
                    run.Status = step.Status;
                    run.StatusText = step.StatusText;
                }
            }
            build.StepRuns = [.. build.StepRuns];
        }

        var agent = await db.Agents.FirstOrDefaultAsync(a => a.Id == agentId, cancellationToken);
        if (agent is not null && agent.CurrentBuildId == build.Id)
        {
            agent.CurrentBuildId = null;
        }
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Build {Number} of {Configuration} finished: {Status} {Text}", build.Number, build.BuildConfiguration?.Name, build.Status, build.StatusText);
        await broadcaster.BuildUpdatedAsync(build, cancellationToken);
        if (agent is not null)
        {
            await broadcaster.AgentUpdatedAsync(agent, cancellationToken);
        }
    }

    /// <summary>Called when an agent connection drops while it owns a running build.</summary>
    public async Task AgentLostAsync(Guid agentId, CancellationToken cancellationToken)
    {
        var builds = await db.Builds.Include(b => b.BuildConfiguration)
            .Where(b => b.AgentId == agentId && (b.Status == BuildStatus.Running || b.Status == BuildStatus.Queued))
            .ToListAsync(cancellationToken);
        foreach (var build in builds)
        {
            build.Status = BuildStatus.Error;
            build.StatusText = "Agent disconnected during the build";
            build.FinishedAt = clock.GetUtcNow();
        }
        if (builds.Count > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
            foreach (var build in builds)
            {
                await broadcaster.BuildUpdatedAsync(build, cancellationToken);
            }
        }
    }

    private Task<Build?> LoadAsync(Guid buildId, CancellationToken cancellationToken) =>
        db.Builds.Include(b => b.BuildConfiguration).Include(b => b.Agent).FirstOrDefaultAsync(b => b.Id == buildId, cancellationToken);
}
