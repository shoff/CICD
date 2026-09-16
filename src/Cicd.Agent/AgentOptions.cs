namespace Cicd.Agent;

public sealed class AgentOptions
{
    public const string SectionName = "Agent";

    public string ServerUrl { get; set; } = "http://localhost:8080";
    /// <summary>Unique agent name. Defaults to the machine name.</summary>
    public string Name { get; set; } = Environment.MachineName;
    /// <summary>Shared secret that must match the server's Agents:AuthToken.</summary>
    public string AuthToken { get; set; } = "";
    public string WorkDirectory { get; set; } = "work";
    /// <summary>Extra capabilities to advertise, e.g. "docker": "true".</summary>
    public Dictionary<string, string> Capabilities { get; set; } = new();
    public int HeartbeatSeconds { get; set; } = 30;
    /// <summary>Environment variables from the agent process that must not leak into builds.</summary>
    public List<string> HiddenEnvironmentVariables { get; set; } = ["Agent__AuthToken", "AGENT__AUTHTOKEN"];
}
