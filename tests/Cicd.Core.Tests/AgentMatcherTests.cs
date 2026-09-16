using Cicd.Core.Builds;
using Cicd.Core.Entities;

namespace Cicd.Core.Tests;

public class AgentMatcherTests
{
    [Fact]
    public void Step_types_become_implicit_runner_requirements()
    {
        var configuration = new BuildConfiguration
        {
            Name = "c",
            Steps = [new BuildStep { Name = "a", TypeId = "command-line" }, new BuildStep { Name = "b", TypeId = "dotnet" }],
            VcsRoot = new VcsRoot { Name = "r", ProviderId = "git", Url = "https://example.com/r.git" },
        };
        var requirements = AgentMatcher.EffectiveRequirements(configuration);
        Assert.Equal("*", requirements["runner.command-line"]);
        Assert.Equal("*", requirements["runner.dotnet"]);
        Assert.Equal("*", requirements["vcs.git"]);
    }

    [Fact]
    public void Agent_without_runner_is_rejected_with_explanation()
    {
        var requirements = new Dictionary<string, string> { ["runner.dotnet"] = "*", ["os"] = "linux" };
        var capabilities = new Dictionary<string, string> { ["os"] = "Linux" };
        var unmet = AgentMatcher.Explain(requirements, capabilities);
        Assert.Single(unmet);
        Assert.Contains("runner.dotnet", unmet[0]);
    }

    [Fact]
    public void Exact_negated_and_wildcard_rules()
    {
        var capabilities = new Dictionary<string, string> { ["os"] = "linux", ["docker"] = "true" };
        Assert.True(AgentMatcher.Matches(new Dictionary<string, string> { ["os"] = "LINUX" }, capabilities));
        Assert.True(AgentMatcher.Matches(new Dictionary<string, string> { ["os"] = "!windows" }, capabilities));
        Assert.False(AgentMatcher.Matches(new Dictionary<string, string> { ["os"] = "!linux" }, capabilities));
        Assert.True(AgentMatcher.Matches(new Dictionary<string, string> { ["docker"] = "*" }, capabilities));
        Assert.False(AgentMatcher.Matches(new Dictionary<string, string> { ["gpu"] = "*" }, capabilities));
    }
}
