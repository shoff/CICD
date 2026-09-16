using System.Text.RegularExpressions;

namespace Cicd.Core.Builds;

/// <summary>
/// TeamCity-style %name% substitution. Unknown references are left untouched so that runner-specific
/// placeholders survive. References may nest; expansion stops after a fixed number of passes to break cycles.
/// </summary>
public static partial class ParameterResolver
{
    private const int MaxPasses = 10;

    [GeneratedRegex(@"%([A-Za-z0-9_.\-]+)%")]
    private static partial Regex Reference();

    public static string Resolve(string template, IReadOnlyDictionary<string, string> parameters)
    {
        var current = template;
        for (var pass = 0; pass < MaxPasses; pass++)
        {
            var replaced = false;
            current = Reference().Replace(current, match =>
            {
                if (parameters.TryGetValue(match.Groups[1].Value, out var value) && value != match.Value)
                {
                    replaced = true;
                    return value;
                }
                return match.Value;
            });
            if (!replaced)
            {
                break;
            }
        }
        return current;
    }

    public static Dictionary<string, string> ResolveAll(IReadOnlyDictionary<string, string> parameters)
    {
        var result = new Dictionary<string, string>(parameters, StringComparer.Ordinal);
        foreach (var key in parameters.Keys)
        {
            result[key] = Resolve(parameters[key], result);
        }
        return result;
    }

    public static IReadOnlyList<string> FindUnresolved(string text) =>
        Reference().Matches(text).Select(m => m.Groups[1].Value).Distinct().ToList();
}
