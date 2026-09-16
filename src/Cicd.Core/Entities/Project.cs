namespace Cicd.Core.Entities;

public sealed class Project
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Name { get; set; }
    public string? Description { get; set; }
    public Guid? ParentId { get; set; }
    public Project? Parent { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<BuildConfiguration> BuildConfigurations { get; set; } = [];
    public List<VcsRoot> VcsRoots { get; set; } = [];
}
