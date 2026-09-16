namespace Cicd.Core.Entities;

public sealed class VcsRoot
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public Project? Project { get; set; }
    public required string Name { get; set; }
    /// <summary>Id of the <see cref="Cicd.Plugins.Sdk.IVcsProvider"/> that services this root, e.g. "git".</summary>
    public required string ProviderId { get; set; }
    public required string Url { get; set; }
    public string DefaultBranch { get; set; } = "main";
    /// <summary>Provider-specific settings (credentials, tokens). Stored as jsonb.</summary>
    public Dictionary<string, string> Properties { get; set; } = new();
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
