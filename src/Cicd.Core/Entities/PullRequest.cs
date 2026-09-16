namespace Cicd.Core.Entities;

public sealed class PullRequest
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid VcsRootId { get; set; }
    public VcsRoot? VcsRoot { get; set; }
    public long Number { get; set; }
    public required string Title { get; set; }
    public required string SourceBranch { get; set; }
    public required string TargetBranch { get; set; }
    public required string HeadSha { get; set; }
    public string? CheckoutRef { get; set; }
    public required string Author { get; set; }
    public required string State { get; set; }
    public string? Url { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    /// <summary>Last head SHA for which a build was queued, per build configuration. Prevents duplicate builds.</summary>
    public Dictionary<string, string> LastBuiltShaByConfiguration { get; set; } = new();
}
