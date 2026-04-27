using System;
using System.Diagnostics;
using OpenTelemetry;
using OpenTelemetry.Trace;

namespace Tracelit.Tracing;

/// <summary>
/// Ensures error spans are always exported regardless of the sampling decision.
///
/// How it works together with <see cref="ErrorAlwaysOnSampler"/>:
/// <list type="number">
///   <item>
///     <see cref="ErrorAlwaysOnSampler"/> returns <see cref="SamplingDecision.RecordOnly"/>
///     (never <see cref="SamplingDecision.Drop"/>) for unsampled spans, so this processor's
///     <see cref="OnEnd"/> is called for every span.
///   </item>
///   <item>
///     On span end, if the activity ended in error and was <em>not</em> sampled
///     (i.e. <c>ActivityTraceFlags.Recorded</c> bit is not set), it is force-exported
///     directly through a dedicated OTLP exporter, bypassing the batch processor.
///   </item>
///   <item>
///     <see cref="BatchActivityExportProcessor"/> ignores <see cref="SamplingDecision.RecordOnly"/>
///     spans, so there is no double-export for error spans on sampled traces.
///   </item>
/// </list>
///
/// <para>
/// This processor owns its own <see cref="BaseExporter{T}"/> instance — it is never
/// shared with <see cref="BatchActivityExportProcessor"/>. Sharing a single exporter
/// between a batch processor (background-thread export) and this processor (inline
/// request-thread export) creates race conditions and use-after-dispose bugs because
/// <see cref="BatchActivityExportProcessor"/> calls <c>Shutdown</c>/<c>Dispose</c>
/// on its exporter when the tracer provider is torn down.
/// </para>
/// </summary>
internal sealed class ErrorSpanProcessor : BaseProcessor<Activity>
{
    private readonly BaseExporter<Activity> _exporter;

    /// <param name="exporter">
    /// A dedicated OTLP exporter instance owned exclusively by this processor.
    /// Must NOT be shared with <see cref="BatchActivityExportProcessor"/>.
    /// </param>
    public ErrorSpanProcessor(BaseExporter<Activity> exporter)
    {
        _exporter = exporter ?? throw new ArgumentNullException(nameof(exporter));
    }

    /// <summary>
    /// Called by the SDK when a span ends. Exports error spans that were not
    /// sampled and would otherwise be silently dropped.
    /// </summary>
    public override void OnEnd(Activity activity)
    {
        try
        {
            // Only intervene for spans that ended in an error.
            // An error span is one that either has an explicit Error status OR
            // has a recorded exception event (set by RecordException = true in
            // ASP.NET Core / HttpClient instrumentation).
            if (!IsErrorActivity(activity))
                return;

            // If the trace was sampled, the BatchActivityExportProcessor will handle
            // export. Exporting here too would cause double-counting in Tracelit.
            // A span is "sampled" when the Recorded bit is set in its trace flags.
            if ((activity.ActivityTraceFlags & ActivityTraceFlags.Recorded) != 0)
                return;

            // Force-export this unsampled error span so it always appears in Tracelit
            // regardless of the sample ratio configured on the tracer provider.
            _exporter.Export(new Batch<Activity>(new[] { activity }, 1));
        }
        catch
        {
            // Never let processor errors propagate to the application.
        }
    }

    /// <summary>
    /// Flushes the underlying exporter synchronously.
    /// </summary>
    protected override bool OnForceFlush(int timeoutMilliseconds)
        => _exporter.ForceFlush(timeoutMilliseconds);

    /// <summary>
    /// Shuts down and flushes the owned exporter.
    /// </summary>
    protected override bool OnShutdown(int timeoutMilliseconds)
        => _exporter.Shutdown(timeoutMilliseconds);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _exporter.Dispose();

        base.Dispose(disposing);
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns <c>true</c> if the activity represents an error.
    /// Matches spans with an explicit <see cref="ActivityStatusCode.Error"/> status
    /// AND spans that have a recorded exception event — the latter covers cases
    /// where instrumentation calls <c>RecordException</c> without also calling
    /// <c>SetStatus(Error)</c>.
    /// </summary>
    private static bool IsErrorActivity(Activity activity)
    {
        if (activity.Status == ActivityStatusCode.Error)
            return true;

        foreach (var evt in activity.Events)
        {
            if (evt.Name == "exception")
                return true;
        }

        return false;
    }
}
