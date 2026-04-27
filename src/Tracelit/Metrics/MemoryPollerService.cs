using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;

namespace Tracelit.Metrics;

/// <summary>
/// Hosted service that polls process memory (RSS) every 60 seconds and emits
/// it as a <c>process.memory.rss</c> gauge in megabytes.
///
/// Equivalent to Ruby's <c>Tracelit::Metrics.install_memory_poller</c> daemon thread.
///
/// The service is registered automatically by <c>AddTracelit()</c> and runs alongside
/// the application host. It is safe to dispose — errors during polling are swallowed
/// and retried on the next cycle.
/// </summary>
internal sealed class MemoryPollerService : BackgroundService
{
    private const int PollIntervalSeconds = 60;
    private readonly ObservableGauge<double> _memoryGauge;
    private double _lastRssMb;

    public MemoryPollerService(TracelitMetrics metrics)
    {
        // Register the gauge with a callback that reads the last polled value.
        // The callback is invoked on each OTel metric export cycle (every 60 s).
        _memoryGauge = metrics.Gauge(
            "process.memory.rss",
            ObserveMemory,
            description: "Process resident set size (working set)",
            unit: "MB");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Prime the first reading before the first export cycle.
        PollMemory();

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(PollIntervalSeconds));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false);
                PollMemory();
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // Swallow unexpected errors — never crash the host over a memory poll.
            }
        }
    }

    private void PollMemory()
    {
        try
        {
            var process = Process.GetCurrentProcess();
            // WorkingSet64 is the closest .NET equivalent to RSS on both Windows and Unix.
            var rssMb = process.WorkingSet64 / 1024.0 / 1024.0;
            Volatile.Write(ref _lastRssMb, rssMb);
        }
        catch
        {
            // Process.GetCurrentProcess() can fail in sandboxed environments.
        }
    }

    private IEnumerable<Measurement<double>> ObserveMemory()
    {
        var rssMb = Volatile.Read(ref _lastRssMb);
        if (rssMb <= 0) yield break;

        yield return new Measurement<double>(rssMb, new KeyValuePair<string, object?>(
            "process.pid", Environment.ProcessId.ToString()));
    }
}
