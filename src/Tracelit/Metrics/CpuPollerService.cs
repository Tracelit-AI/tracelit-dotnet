using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;

namespace Tracelit.Metrics;

/// <summary>
/// Hosted service that polls process CPU utilisation every 30 seconds and emits
/// it as a <c>process.runtime.cpu.usage</c> gauge in percent (0–100).
///
/// CPU% is computed as the delta of <see cref="Process.TotalProcessorTime"/>
/// divided by the wall-clock elapsed time — the same approach used by the Go
/// and Node Tracelit SDKs. This is cross-platform on .NET (Linux / Windows / macOS).
///
/// The emitted metric name matches what <c>QueryServiceSummary</c> in the API
/// queries for, so the Avg CPU Load widget on the service dashboard shows real data.
///
/// Equivalent to Ruby's <c>Tracelit::Metrics.install_cpu_poller</c> daemon thread.
/// </summary>
internal sealed class CpuPollerService : BackgroundService
{
    private const int PollIntervalSeconds = 30;

    private readonly ObservableGauge<double> _cpuGauge;
    private double _lastCpuPct;

    public CpuPollerService(TracelitMetrics metrics)
    {
        _cpuGauge = metrics.Gauge(
            "process.runtime.cpu.usage",
            ObserveCpu,
            description: "Process CPU utilisation percentage",
            unit: "%");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Prime the baseline without emitting — we need a before/after pair.
        var (lastCpuTime, lastWall) = SampleBaseline();

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(PollIntervalSeconds));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false);
                (lastCpuTime, lastWall) = PollCpu(lastCpuTime, lastWall);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // Swallow unexpected errors — never crash the host over a CPU poll.
            }
        }
    }

    private static (TimeSpan cpuTime, DateTime wall) SampleBaseline()
    {
        try
        {
            var proc = Process.GetCurrentProcess();
            proc.Refresh();
            return (proc.TotalProcessorTime, DateTime.UtcNow);
        }
        catch
        {
            return (TimeSpan.Zero, DateTime.UtcNow);
        }
    }

    private (TimeSpan, DateTime) PollCpu(TimeSpan lastCpuTime, DateTime lastWall)
    {
        try
        {
            var proc = Process.GetCurrentProcess();
            proc.Refresh();

            var now        = DateTime.UtcNow;
            var cpuNow     = proc.TotalProcessorTime;
            var elapsed    = (now - lastWall).TotalSeconds;
            var cpuDelta   = (cpuNow - lastCpuTime).TotalSeconds;

            if (elapsed > 0 && cpuDelta >= 0)
            {
                // cpuDelta accounts for all logical cores, so divide by
                // Environment.ProcessorCount to get a per-core equivalent %.
                var pct = Math.Min(100.0, cpuDelta / elapsed / Environment.ProcessorCount * 100.0);
                Volatile.Write(ref _lastCpuPct, pct);
            }

            return (cpuNow, now);
        }
        catch
        {
            return (lastCpuTime, lastWall);
        }
    }

    private IEnumerable<Measurement<double>> ObserveCpu()
    {
        var pct = Volatile.Read(ref _lastCpuPct);
        if (pct <= 0) yield break;

        yield return new Measurement<double>(pct, new KeyValuePair<string, object?>(
            "process.pid", Environment.ProcessId.ToString()));
    }
}
