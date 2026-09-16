namespace Cicd.Core.Builds;

public static class BuildNumberFormatter
{
    public static string Format(string format, int counter, IReadOnlyDictionary<string, string>? extra = null)
    {
        var parameters = new Dictionary<string, string>(extra ?? new Dictionary<string, string>())
        {
            ["build.counter"] = counter.ToString(),
        };
        var result = ParameterResolver.Resolve(string.IsNullOrWhiteSpace(format) ? "%build.counter%" : format, parameters);
        return string.IsNullOrWhiteSpace(result) ? counter.ToString() : result;
    }
}
