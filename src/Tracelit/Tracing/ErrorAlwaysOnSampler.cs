using System;
using System.Collections.Generic;
using OpenTelemetry.Trace;

namespace Tracelit.Tracing;

/// <summary>
/// A sampler that wraps <see cref="TraceIdRatioBasedSampler"/> but upgrades
/// <see cref="SamplingDecision.Drop"/> decisions to <see cref="SamplingDecision.RecordOnly"/>.
///
/// Without this, spans outside the sampling ratio become NonRecordingSpans and the
/// processor pipeline is never called — meaning <see cref="ErrorSpanProcessor"/> cannot
/// intercept and export error spans that fall outside the sample ratio.
///
/// With <see cref="SamplingDecision.RecordOnly"/>:
/// <list type="bullet">
///   <item>Real spans are created and all processors fire on OnEnd.</item>
///   <item><see cref="BatchActivityExportProcessor"/> ignores them (checks <c>Activity.Recorded</c>).</item>
///   <item><see cref="ErrorSpanProcessor"/> sees them and exports any that end in Error status.</item>
/// </list>
/// </summary>
internal sealed class ErrorAlwaysOnSampler : Sampler
{
    private readonly TraceIdRatioBasedSampler _inner;

    /// <param name="sampleRate">
    /// Fraction of root spans to sample (0.0–1.0). Values outside this range
    /// are clamped by <see cref="TraceIdRatioBasedSampler"/>.
    /// </param>
    public ErrorAlwaysOnSampler(double sampleRate)
    {
        _inner = new TraceIdRatioBasedSampler(sampleRate);
        Description = $"ErrorAlwaysOnSampler{{TraceIdRatioBased({sampleRate})}}";
    }

    /// <inheritdoc />
    public override SamplingResult ShouldSample(in SamplingParameters samplingParameters)
    {
        var result = _inner.ShouldSample(samplingParameters);

        // Sampled or RecordOnly — pass through unchanged.
        if (result.Decision != SamplingDecision.Drop)
            return result;

        // Upgrade Drop → RecordOnly so the processor pipeline fires for every span,
        // giving ErrorSpanProcessor a chance to export error spans on unsampled traces.
        return new SamplingResult(
            SamplingDecision.RecordOnly,
            result.Attributes,
            result.TraceStateString);
    }
}
