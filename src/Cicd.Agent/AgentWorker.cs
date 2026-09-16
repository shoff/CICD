using Cicd.Contracts;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Options;

namespace Cicd.Agent;

public sealed class AgentWorker(BuildExecutor executor, CapabilityCollector capabilities, IOptions<AgentOptions> options, ILogger<AgentWorker> logger) : BackgroundService
{
    private HubConnection? connection;
    private Guid agentId;
    private readonly object gate = new();
    private (Guid BuildId, CancellationTokenSource Cancellation, Task Run)? current;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var serverUrl = options.Value.ServerUrl.TrimEnd('/');
        connection = new HubConnectionBuilder()
            .WithUrl($"{serverUrl}/hubs/agents", http =>
            {
                http.AccessTokenProvider = () => Task.FromResult<string?>(options.Value.AuthToken);
            })
            .WithAutomaticReconnect([TimeSpan.Zero, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30)])
            // Must match the server's hub protocol: enums travel as strings.
            .AddJsonProtocol(o => o.PayloadSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()))
            .Build();

        // The server uses a client invoke and expects a result; returning false or throwing sends the build back to the queue.
        connection.On<BuildJob, bool>(AgentHubMethods.RunBuild, job => OnRunBuild(job, stoppingToken));
        connection.On<Guid>(AgentHubMethods.CancelBuild, OnCancelBuild);
        connection.Reconnected += async _ =>
        {
            logger.LogInformation("Reconnected to server, re-registering");
            await RegisterAsync(stoppingToken);
        };
        connection.Closed += error =>
        {
            logger.LogWarning(error, "Connection to server closed");
            return Task.CompletedTask;
        };

        await ConnectWithRetryAsync(stoppingToken);

        using var heartbeat = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(5, options.Value.HeartbeatSeconds)));
        while (await heartbeat.WaitForNextTickAsync(stoppingToken))
        {
            if (connection.State == HubConnectionState.Connected)
            {
                try
                {
                    await connection.InvokeAsync(ServerHubMethods.Heartbeat, stoppingToken);
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "Heartbeat failed");
                }
            }
            else if (connection.State == HubConnectionState.Disconnected)
            {
                await ConnectWithRetryAsync(stoppingToken);
            }
        }
    }

    private async Task ConnectWithRetryAsync(CancellationToken cancellationToken)
    {
        var delay = TimeSpan.FromSeconds(2);
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await connection!.StartAsync(cancellationToken);
                logger.LogInformation("Connected to {Server}", options.Value.ServerUrl);
                await RegisterAsync(cancellationToken);
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning("Cannot reach server at {Server}: {Message}. Retrying in {Delay}s", options.Value.ServerUrl, ex.Message, delay.TotalSeconds);
                await Task.Delay(delay, cancellationToken);
                delay = TimeSpan.FromSeconds(Math.Min(60, delay.TotalSeconds * 2));
            }
        }
    }

    private async Task RegisterAsync(CancellationToken cancellationToken)
    {
        var registration = new AgentRegistration
        {
            Name = options.Value.Name,
            Version = CapabilityCollector.AgentVersion,
            Capabilities = await capabilities.CollectAsync(cancellationToken),
        };
        var result = await connection!.InvokeAsync<AgentRegistrationResult>(ServerHubMethods.Register, registration, cancellationToken);
        agentId = result.AgentId;
        logger.LogInformation("Registered as {Name} ({AgentId}). Authorized: {Authorized}, enabled: {Enabled}", registration.Name, agentId, result.Authorized, result.Enabled);
        if (!result.Authorized)
        {
            logger.LogWarning("This agent is not authorized yet. Authorize it in the server UI or via POST /api/v1/agents/{AgentId}/authorize", agentId);
        }
    }

    private Task<bool> OnRunBuild(BuildJob job, CancellationToken stoppingToken)
    {
        lock (gate)
        {
            if (current is { Run.IsCompleted: false })
            {
                throw new InvalidOperationException($"Agent is busy with build {current.Value.BuildId}");
            }
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            var run = Task.Run(() => RunAsync(job, cancellation), CancellationToken.None);
            current = (job.BuildId, cancellation, run);
        }
        logger.LogInformation("Accepted build {Number} ({BuildId})", job.BuildNumber, job.BuildId);
        return Task.FromResult(true);
    }

    private async Task RunAsync(BuildJob job, CancellationTokenSource cancellation)
    {
        BuildResult result;
        try
        {
            result = await executor.ExecuteAsync(connection!, job, cancellation.Token);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Executor crashed for build {BuildId}", job.BuildId);
            result = new BuildResult { BuildId = job.BuildId, Status = BuildStatus.Error, StatusText = ex.Message };
        }

        try
        {
            await connection!.InvokeAsync(ServerHubMethods.BuildFinished, result);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to report completion of build {BuildId}", job.BuildId);
        }
        finally
        {
            lock (gate)
            {
                if (current?.BuildId == job.BuildId)
                {
                    current = null;
                }
            }
            cancellation.Dispose();
        }
    }

    private void OnCancelBuild(Guid buildId)
    {
        lock (gate)
        {
            if (current?.BuildId == buildId)
            {
                logger.LogInformation("Cancel requested for build {BuildId}", buildId);
                current.Value.Cancellation.Cancel();
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        if (connection is not null)
        {
            await connection.DisposeAsync();
        }
    }
}
