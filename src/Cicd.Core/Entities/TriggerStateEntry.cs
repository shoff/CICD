namespace Cicd.Core.Entities;

public sealed class TriggerStateEntry
{
    public Guid BuildConfigurationId { get; set; }
    public int TriggerIndex { get; set; }
    public required string Key { get; set; }
    public required string Value { get; set; }
}
