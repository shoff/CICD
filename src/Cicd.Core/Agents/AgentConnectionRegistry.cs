using System.Collections.Concurrent;

namespace Cicd.Core.Agents;

/// <summary>In-memory map of connected agents. Rebuilt from live connections; nothing here is persisted.</summary>
public sealed class AgentConnectionRegistry
{
    private readonly ConcurrentDictionary<Guid, string> connectionByAgent = new();
    private readonly ConcurrentDictionary<string, Guid> agentByConnection = new();

    public IReadOnlyCollection<Guid> ConnectedAgentIds => connectionByAgent.Keys.ToList();

    public void Bind(Guid agentId, string connectionId)
    {
        if (connectionByAgent.TryGetValue(agentId, out var previous) && previous != connectionId)
        {
            agentByConnection.TryRemove(previous, out _);
        }
        connectionByAgent[agentId] = connectionId;
        agentByConnection[connectionId] = agentId;
    }

    public Guid? Unbind(string connectionId)
    {
        if (!agentByConnection.TryRemove(connectionId, out var agentId))
        {
            return null;
        }
        connectionByAgent.TryRemove(new KeyValuePair<Guid, string>(agentId, connectionId));
        return agentId;
    }

    public string? GetConnectionId(Guid agentId) => connectionByAgent.TryGetValue(agentId, out var id) ? id : null;
    public Guid? GetAgentId(string connectionId) => agentByConnection.TryGetValue(connectionId, out var id) ? id : null;
    public bool IsConnected(Guid agentId) => connectionByAgent.ContainsKey(agentId);
}
