using Cicd.Core.Agents;
using Cicd.Core.Builds;
using Cicd.Core.PullRequests;
using Cicd.Core.Triggers;
using Cicd.Plugins.Sdk;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Cicd.Core.Services;

public static class CoreServiceCollectionExtensions
{
    /// <summary>Registers orchestration services. The host must also register a <see cref="CicdDbContext"/>, an <see cref="IAgentChannel"/> and an <see cref="IBuildEventPublisher"/>.</summary>
    public static IServiceCollection AddCicdCore(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<CicdServerOptions>(configuration.GetSection(CicdServerOptions.SectionName));
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IBuildEventPublisher, NullBuildEventPublisher>();
        services.AddSingleton<AgentConnectionRegistry>();
        services.AddScoped<BuildQueueService>();
        services.AddScoped<BuildJobFactory>();
        services.AddScoped<BuildProgressService>();
        services.AddScoped<BuildEventBroadcaster>();
        services.AddScoped<AgentService>();
        services.AddScoped<WebhookService>();
        services.AddScoped<ArtifactStore>();
        services.AddSingleton<IBuildTrigger, VcsTrigger>();
        services.AddSingleton<INotifier, PullRequestStatusNotifier>();
        services.AddSingleton<BuildDispatcher>();
        services.AddHostedService(sp => sp.GetRequiredService<BuildDispatcher>());
        services.AddSingleton<TriggerService>();
        services.AddHostedService(sp => sp.GetRequiredService<TriggerService>());
        services.AddSingleton<PullRequestService>();
        services.AddHostedService(sp => sp.GetRequiredService<PullRequestService>());
        return services;
    }
}
