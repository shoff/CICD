using Cicd.Server.Security;

namespace Cicd.Server.Tests;

public class ReturnUrlTests
{
    [Theory]
    [InlineData("/", true)]
    [InlineData("/builds", true)]
    [InlineData("/builds?take=5&status=Failure", true)]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("builds", false)]
    [InlineData("//evil.com", false)]
    [InlineData("/\\evil.com", false)]
    [InlineData("/\t/evil.com", false)]
    [InlineData("/\n", false)]
    [InlineData("https://evil.com", false)]
    [InlineData("javascript:alert(1)", false)]
    public void Only_same_host_absolute_paths_are_local(string? url, bool expected) => Assert.Equal(expected, ReturnUrl.IsLocal(url));

    [Fact]
    public void Sanitize_falls_back_to_root()
    {
        Assert.Equal("/projects", ReturnUrl.Sanitize("/projects"));
        Assert.Equal("/", ReturnUrl.Sanitize("//evil.com"));
    }
}
