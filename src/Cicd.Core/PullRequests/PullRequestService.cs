using Cicd.Core.Builds;
using Cicd.Core.Entities;
using Cicd.Core.Persistence;
using Cicd.Plugins.Sdk;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cicd.Core.PullRequests;

/// <summary>
/// Equivalent of TeamCity's "Pull Requests" build feature: discovers open pull requests on every VCS root used by a
/// configuration with the feature enabled, and queues a build whenever a pull request's head moves.
/// </summary>
public sealed class PullRequestService(IServiceScopeFactory scopeFactory, IOptionsMonitor<CicdServerOptions> options, TimeProvider clock, ILogger<PullRequestService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var interval = TimeSpan.FromSeconds(Math.Max(10, options.CurrentValue.PullRequestPollIntervalSeconds));
            await Task.Delay(interval, stoppingToken);
            try
            {
                await RefreshAsync(null, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Pull request polling failed");
            }
        }
    }

    /// <summary>Refresh all roots, or only the ones whose URL matches <paramref name="repositoryUrl"/>.</summary>
    public async Task<int> RefreshAsync(string? repositoryUrl, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CicdDbContext>();
        var queue = scope.ServiceProvider.GetRequiredService<BuildQueueService>();
        var providers = scope.ServiceProvider.GetServices<IPullRequestProvider>().ToList();
        if (providers.Count == 0)
        {
            return 0;
        }

        var configurations = await db.BuildConfigurations
            .Include(c => c.VcsRoot)
            .Where(c => !c.Paused && c.VcsRootId != null)
            .ToListAsync(cancellationToken);
        configurations = configurations.Where(c => c.PullRequests.Enabled).ToList();
        if (repositoryUrl is not null)
        {
            configurations = configurations.Where(c => RepositoryUrl.Equivalent(c.VcsRoot!.Url, repositoryUrl)).ToList();
        }

        var queued = 0;
        foreach (var group in configurations.GroupBy(c => c.VcsRootId!.Value))
        {
            var root = group.First().VcsRoot!;
            var rootInfo = root.ToInfo();
            var provider = providers.FirstOrDefault(p => p.Supports(rootInfo));
            if (provider is null)
            {
                logger.LogWarning("No pull request provider supports VCS root {Root} ({Url})", root.Name, root.Url);
                continue;
            }

            IReadOnlyList<PullRequestInfo> open;
            try
            {
                open = await provider.ListOpenPullRequestsAsync(rootInfo, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Provider {Provider} failed to list pull requests for {Url}", provider.Id, root.Url);
                continue;
            }

            var known = await db.PullRequests.Where(p => p.VcsRootId == root.Id).ToDictionaryAsync(p => p.Number, cancellationToken);
            foreach (var info in open)
            {
                if (!known.TryGetValue(info.Number, out var entity))
                {
                    entity = new PullRequest
                    {
                        VcsRootId = root.Id,
                        Number = info.Number,
                        Title = info.Title,
                        SourceBranch = info.SourceBranch,
                        TargetBranch = info.TargetBranch,
                        HeadSha = info.HeadSha,
                        Author = info.Author,
                        State = info.State,
                    };
                    db.PullRequests.Add(entity);
                    known[info.Number] = entity;
                }
                entity.Title = info.Title;
                entity.SourceBranch = info.SourceBranch;
                entity.TargetBranch = info.TargetBranch;
                entity.HeadSha = info.HeadSha;
                entity.CheckoutRef = info.CheckoutRef;
                entity.Author = info.Author;
                entity.State = info.State;
                entity.Url = info.Url;
                entity.UpdatedAt = info.UpdatedAt;
            }
            foreach (var stale in known.Values.Where(p => p.State == "open" && open.All(o => o.Number != p.Number)))
            {
                stale.State = "closed";
                stale.UpdatedAt = clock.GetUtcNow();
            }
            await db.SaveChangesAsync(cancellationToken);

            foreach (var configuration in group)
            {
                foreach (var pullRequest in known.Values.Where(p => p.State == "open"))
                {
                    if (!GlobMatcher.IsMatch(configuration.PullRequests.TargetBranchFilter, pullRequest.TargetBranch))
                    {
                        continue;
                    }
                    var key = configuration.Id.ToString();
                    if (pullRequest.LastBuiltShaByConfiguration.TryGetValue(key, out var builtSha) && builtSha == pullRequest.HeadSha)
                    {
                        continue;
                    }
                    await queue.QueueAsync(new QueueBuildCommand(
                        configuration.Id,
                        Branch: $"pull/{pullRequest.Number}",
                        Revision: pullRequest.HeadSha,
                        CheckoutRef: pullRequest.CheckoutRef ?? pullRequest.SourceBranch,
                        TriggeredBy: $"pull request #{pullRequest.Number}",
                        PullRequestId: pullRequest.Id), cancellationToken);
                    pullRequest.LastBuiltShaByConfiguration = new Dictionary<string, string>(pullRequest.LastBuiltShaByConfiguration) { [key] = pullRequest.HeadSha };
                    queued++;
                }
            }
            await db.SaveChangesAsync(cancellationToken);
        }
        return queued;
    }
}

public static class RepositoryUrl
{
    /// <summary>Compares repository URLs loosely: scheme, credentials, trailing slash and ".git" are ignored; ssh and https forms unify.</summary>
    public static bool Equivalent(string left, string right) => string.Equals(Normalize(left), Normalize(right), StringComparison.OrdinalIgnoreCase);

    public static string Normalize(string url)
    {
        var value = url.Trim();
        if (value.StartsWith("git@", StringComparison.OrdinalIgnoreCase))
        {
            value = value[4..].Replace(':', '/');
        }
        else if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            value = uri.Host + uri.AbsolutePath;
        }
        value = value.TrimEnd('/');
        if (value.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            value = value[..^4];
        }
        return value;
    }
}
