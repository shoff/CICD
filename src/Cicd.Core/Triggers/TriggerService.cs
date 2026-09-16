using Cicd.Core.Builds;
using Cicd.Core.Persistence;
using Cicd.Plugins.Sdk;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cicd.Core.Triggers;

/// <summary>Periodically evaluates every configured trigger and queues the builds they request.</summary>
public sealed class TriggerService(IServiceScopeFactory scopeFactory, IOptions<CicdServerOptions> options, TimeProvider clock, ILogger<TriggerService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(5, options.Value.TriggerPollIntervalSeconds));
        using var timer = new PeriodicTimer(interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await EvaluateAllAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Trigger evaluation failed");
            }
        }
    }

    public async Task<int> EvaluateAllAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CicdDbContext>();
        var queue = scope.ServiceProvider.GetRequiredService<BuildQueueService>();
        var triggers = scope.ServiceProvider.GetServices<IBuildTrigger>().ToDictionary(t => t.TypeId, StringComparer.OrdinalIgnoreCase);
        var vcsProviders = scope.ServiceProvider.GetServices<IVcsProvider>().ToDictionary(p => p.Id, StringComparer.OrdinalIgnoreCase);

        var configurations = await db.BuildConfigurations
            .Include(c => c.VcsRoot)
            .Where(c => !c.Paused)
            .ToListAsync(cancellationToken);

        var queued = 0;
        foreach (var configuration in configurations)
        {
            for (var index = 0; index < configuration.Triggers.Count; index++)
            {
                var setting = configuration.Triggers[index];
                if (!triggers.TryGetValue(setting.TypeId, out var trigger))
                {
                    logger.LogWarning("Configuration {Configuration} uses unknown trigger type {Type}", configuration.Name, setting.TypeId);
                    continue;
                }
                var rootInfo = configuration.VcsRoot?.ToInfo();
                IVcsProvider? provider = null;
                if (rootInfo is not null)
                {
                    vcsProviders.TryGetValue(rootInfo.ProviderId, out provider);
                }
                var context = new TriggerContext
                {
                    Configuration = new BuildConfigurationInfo(configuration.Id, configuration.ProjectId, configuration.Name, rootInfo, configuration.Parameters),
                    Trigger = new TriggerDefinition(setting.TypeId, setting.Parameters),
                    State = new DbTriggerState(db, configuration.Id, index),
                    VcsProvider = provider,
                    Now = clock.GetUtcNow(),
                };
                try
                {
                    foreach (var request in await trigger.EvaluateAsync(context, cancellationToken))
                    {
                        await queue.QueueAsync(new QueueBuildCommand(configuration.Id, request.Branch, request.Revision, TriggeredBy: request.Reason), cancellationToken);
                        queued++;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Trigger {Type} failed for configuration {Configuration}", setting.TypeId, configuration.Name);
                }
            }
        }
        return queued;
    }
}
