using Cicd.Contracts;
using Microsoft.Extensions.Logging;

namespace Cicd.Plugins.Sdk;

/// <summary>
/// Server-side description of a step type: identity, display metadata and the parameters it accepts.
/// Used by the UI and API to validate configurations. Paired with an <see cref="IBuildRunner"/> of the same
/// <see cref="Id"/> on the agent side.
/// </summary>
public interface IBuildStepType
{
    string Id { get; }
    string DisplayName { get; }
    string Description { get; }
    IReadOnlyList<ParameterDefinition> Parameters { get; }

    /// <summary>Return validation errors for the given parameters. Empty means valid.</summary>
    IReadOnlyList<string> Validate(IReadOnlyDictionary<string, string> parameters) => [];
}

/// <summary>Agent-side executor for a step type.</summary>
public interface IBuildRunner
{
    /// <summary>Must match the <see cref="IBuildStepType.Id"/> it executes.</summary>
    string TypeId { get; }

    Task<StepResult> RunAsync(BuildStepContext context, CancellationToken cancellationToken);
}

public sealed record StepResult(StepStatus Status, string? StatusText = null)
{
    public static StepResult Success(string? text = null) => new(StepStatus.Success, text);
    public static StepResult Failure(string text) => new(StepStatus.Failure, text);
}

public sealed class BuildStepContext
{
    public required BuildJob Job { get; init; }
    public required BuildStepDefinition Step { get; init; }
    /// <summary>Absolute path of the checkout directory for this build.</summary>
    public required string WorkingDirectory { get; init; }
    /// <summary>Per-build scratch directory outside the checkout, cleaned after the build.</summary>
    public required string TempDirectory { get; init; }
    public required IBuildLog Log { get; init; }
    /// <summary>Environment variables for child processes: agent env + build parameters.</summary>
    public required IReadOnlyDictionary<string, string> Environment { get; init; }
    public required ILogger Logger { get; init; }
}

/// <summary>Streams output back to the server for real-time display.</summary>
public interface IBuildLog
{
    void Write(Contracts.LogLevel level, string text);
}

public static class BuildLogExtensions
{
    public static void Info(this IBuildLog log, string text) => log.Write(Contracts.LogLevel.Info, text);
    public static void Warning(this IBuildLog log, string text) => log.Write(Contracts.LogLevel.Warning, text);
    public static void Error(this IBuildLog log, string text) => log.Write(Contracts.LogLevel.Error, text);
    public static void Debug(this IBuildLog log, string text) => log.Write(Contracts.LogLevel.Debug, text);
}
