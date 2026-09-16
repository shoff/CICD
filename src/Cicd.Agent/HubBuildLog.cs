using System.Threading.Channels;
using Cicd.Contracts;
using Cicd.Plugins.Sdk;
using Microsoft.AspNetCore.SignalR.Client;

namespace Cicd.Agent;

/// <summary>
/// Buffers log lines and ships them to the server in small batches so a chatty build does not
/// turn into one hub call per line. Lines are sequenced so the server and UI can order and resume them.
/// </summary>
public sealed class HubBuildLog : IBuildLog, IAsyncDisposable
{
    private static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(400);
    private const int MaxBatch = 200;

    private readonly HubConnection connection;
    private readonly Guid buildId;
    private readonly ILogger logger;
    private readonly Channel<LogLine> channel = Channel.CreateUnbounded<LogLine>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Task pump;
    private long sequence;

    public int? CurrentStepIndex { get; set; }

    public HubBuildLog(HubConnection connection, Guid buildId, ILogger logger)
    {
        this.connection = connection;
        this.buildId = buildId;
        this.logger = logger;
        pump = Task.Run(PumpAsync);
    }

    public void Write(Contracts.LogLevel level, string text)
    {
        var line = new LogLine
        {
            Sequence = Interlocked.Increment(ref sequence) - 1,
            Timestamp = DateTimeOffset.UtcNow,
            Level = level,
            StepIndex = CurrentStepIndex,
            Text = text,
        };
        channel.Writer.TryWrite(line);
    }

    private async Task PumpAsync()
    {
        var reader = channel.Reader;
        var batch = new List<LogLine>(MaxBatch);
        while (await reader.WaitToReadAsync())
        {
            var deadline = DateTime.UtcNow + FlushInterval;
            while (batch.Count < MaxBatch && DateTime.UtcNow < deadline)
            {
                if (reader.TryRead(out var line))
                {
                    batch.Add(line);
                }
                else
                {
                    await Task.Delay(20);
                    if (reader.Completion.IsCompleted) break;
                }
            }
            if (batch.Count > 0)
            {
                await SendAsync(batch);
                batch.Clear();
            }
        }
        while (reader.TryRead(out var remaining))
        {
            batch.Add(remaining);
        }
        if (batch.Count > 0)
        {
            await SendAsync(batch);
        }
    }

    private async Task SendAsync(List<LogLine> batch)
    {
        try
        {
            await connection.InvokeAsync(ServerHubMethods.Log, buildId, batch.ToArray());
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to ship {Count} log line(s) for build {BuildId}", batch.Count, buildId);
        }
    }

    public async ValueTask DisposeAsync()
    {
        channel.Writer.TryComplete();
        await pump;
    }
}
