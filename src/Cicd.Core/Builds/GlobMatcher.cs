using System.Text.RegularExpressions;

namespace Cicd.Core.Builds;

/// <summary>Minimal glob for branch filters: "*" any run, "?" one char, "+:" include and "-:" exclude prefixes, newline separated.</summary>
public static class GlobMatcher
{
    public static bool IsMatch(string filter, string value)
    {
        if (string.IsNullOrWhiteSpace(filter))
        {
            return true;
        }
        var include = false;
        var anyInclude = false;
        foreach (var rawRule in filter.Split(['\n', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var exclude = rawRule.StartsWith("-:");
            var rule = rawRule.StartsWith("+:") || exclude ? rawRule[2..] : rawRule;
            var matches = ToRegex(rule).IsMatch(value);
            if (exclude)
            {
                if (matches)
                {
                    return false;
                }
            }
            else
            {
                anyInclude = true;
                include |= matches;
            }
        }
        return !anyInclude || include;
    }

    private static Regex ToRegex(string glob)
    {
        var pattern = "^" + Regex.Escape(glob).Replace(@"\*", ".*").Replace(@"\?", ".") + "$";
        return new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
