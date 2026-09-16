using Cicd.Contracts;
using Cicd.Plugins.Sdk;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Options;

namespace Cicd.Agent;

public sealed class BuildExecutor(
    IEnumerable<IBuildRunner> runners,
    IEnumerable<IVcsCheckout> checkouts,
    ArtifactUploader artifacts,
    IOptions<AgentOptions> options,
    ILoggerFactory loggerFactory,
    ILogger<BuildExecutor> logger)
{
    public async Task<BuildResult> ExecuteAsync(HubConnection connection, BuildJob job, CancellationToken cancellationToken)
    {
        await using var log = new HubBuildLog(connection, job.BuildId, logger);
        var stepResults = job.Steps.Select(s => new StepProgress { BuildId = job.BuildId, StepIndex = s.Index, Status = StepStatus.Pending }).ToList();
        var status = BuildStatus.Success;
        string? statusText = null;

        var workRoot = Path.GetFullPath(options.Value.WorkDirectory);
        var checkoutDirectory = Path.Combine(workRoot, job.BuildConfigurationId.ToString("N"));
        var tempDirectory = Path.Combine(workRoot, "temp", job.BuildId.ToString("N"));
        Directory.CreateDirectory(checkoutDirectory);
        Directory.CreateDirectory(tempDirectory);

        try
        {
            await connection.InvokeAsync(ServerHubMethods.BuildStarted, job.BuildId, cancellationToken);
            log.Info($"Build #{job.BuildNumber} of {job.ProjectName} / {job.BuildConfigurationName} started on agent {options.Value.Name}");

            var environment = BuildEnvironment(job, checkoutDirectory, tempDirectory);

            if (job.Checkout is { } checkout)
            {
                var provider = checkouts.FirstOrDefault(c => string.Equals(c.ProviderId, checkout.ProviderId, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException($"No checkout provider for '{checkout.ProviderId}' is installed on this agent.");
                log.Info($"Checking out {checkout.Url} ({checkout.Branch}{(checkout.Revision is null ? "" : "@" + checkout.Revision)})");
                var revision = await provider.CheckoutAsync(checkout, checkoutDirectory, log, cancellationToken);
                environment["BUILD_VCS_NUMBER"] = revision;
                log.Info($"Checked out revision {revision}");
            }

            var anyFailed = false;
            foreach (var step in job.Steps)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var progress = stepResults[step.Index];
                var shouldRun = step.ExecutionPolicy switch
                {
                    StepExecutionPolicy.Always => true,
                    StepExecutionPolicy.OnlyIfPreviousFailed => anyFailed,
                    _ => !anyFailed,
                };
                if (!shouldRun)
                {
                    stepResults[step.Index] = progress with { Status = StepStatus.Skipped };
                    await ReportStepAsync(connection, stepResults[step.Index]);
                    continue;
                }

                log.CurrentStepIndex = step.Index;
                log.Info($"==> Step {step.Index + 1}/{job.Steps.Count}: {step.Name} ({step.TypeId})");
                stepResults[step.Index] = progress with { Status = StepStatus.Running };
                await ReportStepAsync(connection, stepResults[step.Index]);

                StepResult result;
                try
                {
                    var runner = runners.FirstOrDefault(r => string.Equals(r.TypeId, step.TypeId, StringComparison.OrdinalIgnoreCase))
                        ?? throw new InvalidOperationException($"No runner for step type '{step.TypeId}' is installed on this agent.");
                    var context = new BuildStepContext
                    {
                        Job = job,
                        Step = step,
                        WorkingDirectory = checkoutDirectory,
                        TempDirectory = tempDirectory,
                        Log = log,
                        Environment = environment,
                        Logger = loggerFactory.CreateLogger(runner.GetType()),
                    };
                    result = await runner.RunAsync(context, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    log.Error($"Step failed with exception: {ex.GetBaseException().Message}");
                    logger.LogError(ex, "Step {Step} of build {BuildId} threw", step.Name, job.BuildId);
                    result = StepResult.Failure(ex.GetBaseException().Message);
                }

                stepResults[step.Index] = progress with { Status = result.Status, StatusText = result.StatusText };
                await ReportStepAsync(connection, stepResults[step.Index]);
                if (result.Status == StepStatus.Failure)
                {
                    anyFailed = true;
                    log.Error($"<== Step '{step.Name}' failed: {result.StatusText}");
                    statusText ??= $"Step '{step.Name}' failed: {result.StatusText}";
                }
                else
                {
                    log.Info($"<== Step '{step.Name}' finished: {result.Status}");
                }
            }
            log.CurrentStepIndex = null;

            if (anyFailed)
            {
                status = BuildStatus.Failure;
            }

            if (job.ArtifactPaths.Count > 0)
            {
                var published = await artifacts.UploadAsync(job.BuildId, checkoutDirectory, job.ArtifactPaths, log, cancellationToken);
                log.Info($"Published {published} artifact(s)");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            status = BuildStatus.Canceled;
            statusText = "Canceled";
            log.Warning("Build canceled");
            for (var i = 0; i < stepResults.Count; i++)
            {
                if (stepResults[i].Status is StepStatus.Running or StepStatus.Pending)
                {
                    stepResults[i] = stepResults[i] with { Status = StepStatus.Canceled };
                }
            }
        }
        catch (Exception ex)
        {
            status = BuildStatus.Error;
            statusText = ex.GetBaseException().Message;
            log.Error($"Build error: {statusText}");
            logger.LogError(ex, "Build {BuildId} errored", job.BuildId);
        }
        finally
        {
            try { Directory.Delete(tempDirectory, recursive: true); } catch { /* best effort */ }
        }

        log.Info($"Build finished: {status}{(statusText is null ? "" : " - " + statusText)}");
        await log.DisposeAsync();
        return new BuildResult { BuildId = job.BuildId, Status = status, StatusText = statusText, Steps = stepResults };
    }

    private Dictionary<string, string> BuildEnvironment(BuildJob job, string checkoutDirectory, string tempDirectory)
    {
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var hidden = options.Value.HiddenEnvironmentVariables;
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            var key = entry.Key.ToString()!;
            if (!hidden.Contains(key, StringComparer.OrdinalIgnoreCase) && entry.Value is not null)
            {
                environment[key] = entry.Value.ToString()!;
            }
        }
        foreach (var (key, value) in job.Parameters)
        {
            // TeamCity exposes env.* parameters verbatim; other parameters get a CICD_ prefix with dots mapped to underscores.
            var name = key.StartsWith("env.", StringComparison.OrdinalIgnoreCase) ? key[4..] : "CICD_" + key.Replace('.', '_').ToUpperInvariant();
            environment[name] = value;
        }
        environment["BUILD_NUMBER"] = job.BuildNumber;
        environment["BUILD_ID"] = job.BuildId.ToString();
        environment["CI"] = "true";
        environment["CICD_CHECKOUT_DIR"] = checkoutDirectory;
        environment["CICD_TEMP_DIR"] = tempDirectory;
        return environment;
    }

    private async Task ReportStepAsync(HubConnection connection, StepProgress progress)
    {
        try
        {
            await connection.InvokeAsync(ServerHubMethods.StepChanged, progress);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to report step progress for build {BuildId}", progress.BuildId);
        }
    }
}
