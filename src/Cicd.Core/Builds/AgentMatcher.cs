using Cicd.Core.Entities;

namespace Cicd.Core.Builds;

/// <summary>
/// Decides whether an agent can run a build configuration. Requirement values: "*" means the capability must exist,
/// a leading "!" means it must not equal the rest, anything else is a case-insensitive exact match. Step types used by
/// the configuration are implicitly required as "runner.&lt;typeId&gt;" capabilities so a build never lands on an
/// agent that lacks the matching runner plugin.
/// </summary>
public static class AgentMatcher
{
    public const string RunnerCapabilityPrefix = "runner.";
    public const string VcsCapabilityPrefix = "vcs.";

    public static Dictionary<string, string> EffectiveRequirements(BuildConfiguration configuration)
    {
        var requirements = new Dictionary<string, string>(configuration.AgentRequirements, StringComparer.OrdinalIgnoreCase);
        foreach (var typeId in configuration.Steps.Select(s => s.TypeId).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            requirements.TryAdd(RunnerCapabilityPrefix + typeId, "*");
        }
        if (configuration.VcsRoot is not null)
        {
            requirements.TryAdd(VcsCapabilityPrefix + configuration.VcsRoot.ProviderId, "*");
        }
        return requirements;
    }

    public static bool Matches(IReadOnlyDictionary<string, string> requirements, IReadOnlyDictionary<string, string> capabilities) =>
        Explain(requirements, capabilities).Count == 0;

    /// <summary>Returns the list of unmet requirements, empty when the agent is compatible.</summary>
    public static IReadOnlyList<string> Explain(IReadOnlyDictionary<string, string> requirements, IReadOnlyDictionary<string, string> capabilities)
    {
        var lookup = new Dictionary<string, string>(capabilities, StringComparer.OrdinalIgnoreCase);
        var unmet = new List<string>();
        foreach (var (name, expected) in requirements)
        {
            var present = lookup.TryGetValue(name, out var actual);
            if (expected == "*")
            {
                if (!present)
                {
                    unmet.Add($"{name} must exist");
                }
            }
            else if (expected.StartsWith('!'))
            {
                if (present && string.Equals(actual, expected[1..], StringComparison.OrdinalIgnoreCase))
                {
                    unmet.Add($"{name} must not be '{expected[1..]}'");
                }
            }
            else if (!present || !string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            {
                unmet.Add($"{name} must be '{expected}' (agent has '{actual ?? "<missing>"}')");
            }
        }
        return unmet;
    }
}
