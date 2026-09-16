using Cicd.Core.Builds;
using Cicd.Core.Persistence;
using Cicd.Core.PullRequests;
using Cicd.Plugins.Sdk;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Cicd.Core.Services;

public sealed class WebhookService(CicdDbContext db, BuildQueueService queue, PullRequestService pullRequests, IEnumerable<IWebhookHandler> handlers, ILogger<WebhookService> logger)
{
    public bool HasHandler(string providerId) => handlers.Any(h => string.Equals(h.ProviderId, providerId, StringComparison.OrdinalIgnoreCase));

    public async Task<IReadOnlyList<string>> HandleAsync(WebhookRequest request, CancellationToken cancellationToken)
    {
        var handler = handlers.FirstOrDefault(h => string.Equals(h.ProviderId, request.ProviderId, StringComparison.OrdinalIgnoreCase))
            ?? throw new KeyNotFoundException($"No webhook handler for '{request.ProviderId}'.");

        var outcomes = new List<string>();
        foreach (var action in await handler.HandleAsync(request, cancellationToken))
        {
            switch (action)
            {
                case WebhookAction.QueueBuild queueBuild:
                    outcomes.AddRange(await QueueForRepositoryAsync(queueBuild, cancellationToken));
                    break;
                case WebhookAction.RefreshPullRequests refresh:
                    var count = await pullRequests.RefreshAsync(refresh.RepositoryUrl, cancellationToken);
                    outcomes.Add($"refreshed pull requests for {refresh.RepositoryUrl}: {count} build(s) queued");
                    break;
            }
        }
        return outcomes;
    }

    private async Task<IReadOnlyList<string>> QueueForRepositoryAsync(WebhookAction.QueueBuild action, CancellationToken cancellationToken)
    {
        var configurations = await db.BuildConfigurations.Include(c => c.VcsRoot).Where(c => !c.Paused && c.VcsRootId != null).ToListAsync(cancellationToken);
        var matching = configurations.Where(c => RepositoryUrl.Equivalent(c.VcsRoot!.Url, action.RepositoryUrl)).ToList();
        if (matching.Count == 0)
        {
            logger.LogInformation("Webhook for {Url} matched no build configuration", action.RepositoryUrl);
            return [$"no configuration uses {action.RepositoryUrl}"];
        }
        var outcomes = new List<string>();
        foreach (var configuration in matching)
        {
            // Only configurations with a VCS trigger react to pushes, matching TeamCity's semantics.
            if (!configuration.Triggers.Any(t => string.Equals(t.TypeId, Triggers.VcsTrigger.Type, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }
            var build = await queue.QueueAsync(new QueueBuildCommand(configuration.Id, action.Branch, action.Revision, TriggeredBy: action.Reason), cancellationToken);
            outcomes.Add($"queued {configuration.Name} #{build.Number}");
        }
        return outcomes;
    }
}
