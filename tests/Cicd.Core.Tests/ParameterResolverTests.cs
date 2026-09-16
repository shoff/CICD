using Cicd.Core.Builds;

namespace Cicd.Core.Tests;

public class ParameterResolverTests
{
    [Fact]
    public void Substitutes_known_references_and_leaves_unknown_ones()
    {
        var parameters = new Dictionary<string, string> { ["build.number"] = "42", ["env.NAME"] = "ci" };
        var result = ParameterResolver.Resolve("v%build.number%-%env.NAME%-%missing%", parameters);
        Assert.Equal("v42-ci-%missing%", result);
    }

    [Fact]
    public void Resolves_nested_references()
    {
        var parameters = new Dictionary<string, string> { ["major"] = "1", ["version"] = "%major%.0", ["tag"] = "v%version%" };
        var resolved = ParameterResolver.ResolveAll(parameters);
        Assert.Equal("v1.0", resolved["tag"]);
    }

    [Fact]
    public void Terminates_on_self_reference()
    {
        var parameters = new Dictionary<string, string> { ["loop"] = "%loop%" };
        var result = ParameterResolver.Resolve("%loop%", parameters);
        Assert.Equal("%loop%", result);
    }

    [Fact]
    public void Build_number_format_uses_counter_and_parameters()
    {
        var number = BuildNumberFormatter.Format("1.%minor%.%build.counter%", 7, new Dictionary<string, string> { ["minor"] = "3" });
        Assert.Equal("1.3.7", number);
    }
}
