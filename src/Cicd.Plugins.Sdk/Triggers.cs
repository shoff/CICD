using Cicd.Contracts;

namespace Cicd.Plugins.Sdk;

public sealed record TriggerDefinition(string TypeId, IReadOnlyDictionary<string, string> Parameters);

public sealed record BuildConfigurationInfo(
    Guid Id,
    Guid ProjectId,
    string Name,
    VcsRootInfo? VcsRoot,
    IReadOnlyDictionary<string, string> Parameters);

public sealed record TriggerRequest(string Branch, string? Revision, string Reason);

public sealed class TriggerContext
{
    public required BuildConfigurationInfo Configuration { get; init; }
    public required TriggerDefinition Trigger { get; init; }
    public required ITriggerState State { get; init; }
    public required IVcsProvider? VcsProvider { get; init; }
    public required DateTimeOffset Now { get; init; }
}

/// <summary>Persisted key/value store scoped to (configuration, trigger). Survives restarts.</summary>
public interface ITriggerState
{
    Task<string?> GetAsync(string key, CancellationToken cancellationToken);
    Task SetAsync(string key, string value, CancellationToken cancellationToken);
}

/// <summary>Server-side. Polled periodically; return one request per build that should be queued.</summary>
public interface IBuildTrigger
{
    string TypeId { get; }
    string DisplayName { get; }
    IReadOnlyList<ParameterDefinition> Parameters { get; }

    Task<IReadOnlyList<TriggerRequest>> EvaluateAsync(TriggerContext context, CancellationToken cancellationToken);
}

public sealed record BuildEvent(
    Guid BuildId,
    Guid BuildConfigurationId,
    string BuildConfigurationName,
    string BuildNumber,
    BuildStatus Status,
    string? StatusText,
    string Branch,
    string? Revision,
    DateTimeOffset Timestamp);

/// <summary>Server-side. Receives every build state transition.</summary>
public interface INotifier
{
    string Id { get; }
    Task OnBuildEventAsync(BuildEvent buildEvent, CancellationToken cancellationToken);
}

public sealed record WebhookRequest(
    string ProviderId,
    IReadOnlyDictionary<string, string> Headers,
    string Body);

public abstract record WebhookAction
{
    public sealed record QueueBuild(string RepositoryUrl, string Branch, string? Revision, string Reason) : WebhookAction;
    public sealed record RefreshPullRequests(string RepositoryUrl) : WebhookAction;
}

/// <summary>Server-side. Turns an inbound HTTP webhook into actions the server applies.</summary>
public interface IWebhookHandler
{
    string ProviderId { get; }
    Task<IReadOnlyList<WebhookAction>> HandleAsync(WebhookRequest request, CancellationToken cancellationToken);
}

/// <summary>Agent-side. Contributes key/value capabilities used for agent requirement matching.</summary>
public interface IAgentCapabilityProvider
{
    Task<IReadOnlyDictionary<string, string>> GetCapabilitiesAsync(CancellationToken cancellationToken);
}
