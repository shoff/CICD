using Microsoft.AspNetCore.SignalR;

namespace Cicd.Server.Hubs;

/// <summary>
/// Real-time feed for browsers and tools. Clients receive BuildUpdated / AgentUpdated globally and BuildLog for
/// builds they subscribed to.
/// </summary>
public sealed class BuildHub : Hub
{
    public static string BuildGroup(Guid buildId) => $"build:{buildId:N}";

    public Task SubscribeToBuild(Guid buildId) => Groups.AddToGroupAsync(Context.ConnectionId, BuildGroup(buildId));
    public Task UnsubscribeFromBuild(Guid buildId) => Groups.RemoveFromGroupAsync(Context.ConnectionId, BuildGroup(buildId));
}
