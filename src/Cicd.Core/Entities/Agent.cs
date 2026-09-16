namespace Cicd.Core.Entities;

public sealed class Agent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Name { get; set; }
    public string Version { get; set; } = "";
    /// <summary>An admin must authorize a new agent before it receives work, as in TeamCity.</summary>
    public bool Authorized { get; set; }
    public bool Enabled { get; set; } = true;
    public Dictionary<string, string> Capabilities { get; set; } = new();
    public Guid? CurrentBuildId { get; set; }
    public DateTimeOffset RegisteredAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastSeenAt { get; set; }
}
