namespace Cicd.Core.Tests;

public sealed class TestClock : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 9, 16, 12, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => Now;
}
