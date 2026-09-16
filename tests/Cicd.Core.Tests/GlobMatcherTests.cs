using Cicd.Core.Builds;
using Cicd.Core.PullRequests;

namespace Cicd.Core.Tests;

public class GlobMatcherTests
{
    [Theory]
    [InlineData("*", "main", true)]
    [InlineData("main", "main", true)]
    [InlineData("release/*", "release/1.2", true)]
    [InlineData("release/*", "feature/x", false)]
    [InlineData("+:*\n-:wip/*", "wip/thing", false)]
    [InlineData("+:*\n-:wip/*", "main", true)]
    [InlineData("", "anything", true)]
    public void Matches_branch_filters(string filter, string branch, bool expected) =>
        Assert.Equal(expected, GlobMatcher.IsMatch(filter, branch));

    [Theory]
    [InlineData("https://github.com/shoff/CICD.git", "git@github.com:shoff/CICD.git")]
    [InlineData("https://github.com/shoff/CICD", "https://user:token@github.com/shoff/CICD.git/")]
    public void Repository_urls_unify(string left, string right) =>
        Assert.True(RepositoryUrl.Equivalent(left, right));

    [Fact]
    public void Different_repositories_do_not_unify() =>
        Assert.False(RepositoryUrl.Equivalent("https://github.com/shoff/CICD", "https://github.com/shoff/other"));
}
