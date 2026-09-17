using Cicd.Contracts;
using Cicd.Core.Builds;
using Cicd.Core.Persistence;
using Cicd.Plugins.Sdk;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cicd.Core.PullRequests;

/// <summary>Equivalent of TeamCity's Commit Status Publisher for pull request builds.</summary>
public sealed class PullRequestStatusNotifier(IServiceScopeFactory scopeFactory, IOptionsMonitor<CicdServerOptions> options, ILogger<PullRequestStatusNotifier> logger) : INotifier
{
    public string Id => "pull-request-status";

    public async Task OnBuildEventAsync(BuildEvent buildEvent, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CicdDbContext>();
        var build = await db.Builds.AsNoTracking()
            .Include(b => b.BuildConfiguration)
            .Include(b => b.PullRequest!).ThenInclude(p => p.VcsRoot)
            .FirstOrDefaultAsync(b => b.Id == buildEvent.BuildId, cancellationToken);
        if (build?.PullRequest?.VcsRoot is null || build.BuildConfiguration is null || !build.BuildConfiguration.PullRequests.ReportStatus || build.Revision is null)
        {
            return;
        }

        var rootInfo = build.PullRequest.VcsRoot.ToInfo();
        var provider = scope.ServiceProvider.GetServices<IPullRequestProvider>().FirstOrDefault(p => p.Supports(rootInfo));
        if (provider is null)
        {
            return;
        }

        var report = new CommitStatusReport(
            build.Revision,
            build.Status,
            Context: $"cicd/{build.BuildConfiguration.Name}",
            Description: build.Status switch
            {
                BuildStatus.Queued => "Build queued",
                BuildStatus.Running => "Build running",
                BuildStatus.Success => "Build succeeded",
                BuildStatus.Canceled => "Build canceled",
                _ => build.StatusText ?? "Build failed",
            },
            TargetUrl: $"{options.CurrentValue.PublicUrl.TrimEnd('/')}/builds/{build.Id}");
        try
        {
            await provider.ReportStatusAsync(rootInfo, report, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Failed to report status for build {BuildId} to {Provider}", build.Id, provider.Id);
        }
    }
}
