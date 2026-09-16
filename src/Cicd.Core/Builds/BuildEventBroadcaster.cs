using Cicd.Contracts;
using Cicd.Core.Entities;
using Cicd.Plugins.Sdk;
using Microsoft.Extensions.Logging;

namespace Cicd.Core.Builds;

/// <summary>Publishes build changes to the UI publisher and to every <see cref="INotifier"/> plugin.</summary>
public sealed class BuildEventBroadcaster(IBuildEventPublisher publisher, IEnumerable<INotifier> notifiers, TimeProvider clock, ILogger<BuildEventBroadcaster> logger)
{
    public async Task BuildUpdatedAsync(Build build, CancellationToken cancellationToken)
    {
        await publisher.BuildUpdatedAsync(build, cancellationToken);

        var buildEvent = new BuildEvent(
            build.Id,
            build.BuildConfigurationId,
            build.BuildConfiguration?.Name ?? "",
            build.Number,
            build.Status,
            build.StatusText,
            build.Branch,
            build.Revision,
            clock.GetUtcNow());

        foreach (var notifier in notifiers)
        {
            try
            {
                await notifier.OnBuildEventAsync(buildEvent, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Notifier {Notifier} failed for build {BuildId}", notifier.Id, build.Id);
            }
        }
    }

    public Task BuildLogAsync(Guid buildId, IReadOnlyList<LogLine> lines, CancellationToken cancellationToken) =>
        publisher.BuildLogAsync(buildId, lines, cancellationToken);

    public Task AgentUpdatedAsync(Agent agent, CancellationToken cancellationToken) =>
        publisher.AgentUpdatedAsync(agent, cancellationToken);
}
