namespace Cicd.Plugins.Sdk;

public enum ParameterKind
{
    Text,
    MultilineText,
    Password,
    Boolean,
    Select,
    Path,
}

public sealed record ParameterDefinition(
    string Name,
    string DisplayName,
    string? Description = null,
    bool Required = false,
    string? DefaultValue = null,
    ParameterKind Kind = ParameterKind.Text,
    IReadOnlyList<string>? Options = null);

public static class ParameterExtensions
{
    public static string Get(this IReadOnlyDictionary<string, string> parameters, string name, string fallback = "") =>
        parameters.TryGetValue(name, out var value) && !string.IsNullOrEmpty(value) ? value : fallback;

    public static string Require(this IReadOnlyDictionary<string, string> parameters, string name) =>
        parameters.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new InvalidOperationException($"Required parameter '{name}' is missing.");

    public static bool GetBool(this IReadOnlyDictionary<string, string> parameters, string name, bool fallback = false) =>
        parameters.TryGetValue(name, out var value) && bool.TryParse(value, out var parsed) ? parsed : fallback;
}
