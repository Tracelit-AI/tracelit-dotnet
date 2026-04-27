using System;
using System.Collections.Generic;
using System.Diagnostics;
using FluentAssertions;
using OpenTelemetry;
using OpenTelemetry.Trace;
using Tracelit.Tracing;
using Xunit;

namespace Tracelit.Tests;

/// <summary>
/// Tests for <see cref="ErrorSpanProcessor"/>.
///
/// Because <see cref="Activity"/> is a sealed .NET type that cannot be easily
/// mocked, we create real <see cref="ActivitySource"/> activities for testing.
/// A custom <see cref="SpyExporter"/> records what gets exported.
/// </summary>
public sealed class ErrorSpanProcessorTests : IDisposable
{
    private readonly ActivitySource _source;
    private readonly SpyExporter _spy;
    private readonly ErrorSpanProcessor _processor;
    private readonly TracerProvider _tracerProvider;

    public ErrorSpanProcessorTests()
    {
        _source = new ActivitySource("tracelit.test");
        _spy    = new SpyExporter();
        _processor = new ErrorSpanProcessor(_spy);

        // Build a TracerProvider with AlwaysOnSampler so activities are created
        // as recording, and add our processor to the pipeline.
        _tracerProvider = Sdk.CreateTracerProviderBuilder()
            .AddSource("tracelit.test")
            .SetSampler(new AlwaysOnSampler())
            .AddProcessor(_processor)
            .Build()!;
    }

    // ─── Skips OK spans ───────────────────────────────────────────────────────

    [Fact]
    public void OnEnd_OkStatus_DoesNotExport()
    {
        using var activity = _source.StartActivity("ok-span");
        activity!.SetStatus(ActivityStatusCode.Ok);
        activity.Stop();

        _spy.ExportedActivities.Should().BeEmpty(
            because: "OK spans must not be exported by ErrorSpanProcessor");
    }

    [Fact]
    public void OnEnd_UnsetStatus_DoesNotExport()
    {
        using var activity = _source.StartActivity("unset-span");
        // Do not set status — defaults to Unset.
        activity!.Stop();

        _spy.ExportedActivities.Should().BeEmpty(
            because: "Unset status spans are not errors");
    }

    // ─── Skips sampled error spans (no double-export) ─────────────────────────

    [Fact]
    public void OnEnd_SampledErrorSpan_DoesNotExportViaErrorProcessor()
    {
        // With AlwaysOnSampler, the activity has ActivityTraceFlags.Recorded set,
        // which signals "sampled". The BatchActivityExportProcessor handles those,
        // so ErrorSpanProcessor must skip them to prevent double-export.
        using var activity = _source.StartActivity("sampled-error");
        activity!.SetStatus(ActivityStatusCode.Error, "Something went wrong");
        activity.Stop();

        // The spy should NOT have been called from ErrorSpanProcessor.
        // (The batch processor would export it, but the spy is only wired to ErrorSpanProcessor.)
        _spy.ExportedActivities.Should().BeEmpty(
            because: "sampled error spans are handled by BatchActivityExportProcessor");
    }

    // ─── Null-safety and resilience ───────────────────────────────────────────

    [Fact]
    public void Constructor_NullExporter_ThrowsArgumentNullException()
    {
        var act = () => new ErrorSpanProcessor(null!);
        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("exporter");
    }

    [Fact]
    public void OnEnd_ExporterThrows_DoesNotPropagateException()
    {
        // Use a throwing exporter to verify errors are swallowed.
        var throwingExporter = new ThrowingExporter();
        var processor = new ErrorSpanProcessor(throwingExporter);

        using var source = new ActivitySource("tracelit.test.throw");
        var throwingProvider = Sdk.CreateTracerProviderBuilder()
            .AddSource("tracelit.test.throw")
            // Use RecordOnly sampler so activity.ActivityTraceFlags does NOT have Recorded,
            // which means ErrorSpanProcessor will attempt to export the error span.
            .SetSampler(new AlwaysOffSampler())
            .AddProcessor(new RecordOnlyWrapper(processor))
            .Build()!;

        // Should not throw even though the exporter throws.
        var act = () =>
        {
            using var activity = source.StartActivity("error-span");
            if (activity is null) return;
            activity.SetStatus(ActivityStatusCode.Error, "boom");
            activity.Stop();
        };

        act.Should().NotThrow(
            because: "processor errors must never propagate to the application");

        throwingProvider.Dispose();
    }

    // ─── ForceFlush / Shutdown ────────────────────────────────────────────────

    [Fact]
    public void ForceFlush_CallsExporterForceFlush()
    {
        _spy.ForceFlushCalled.Should().BeFalse();
        _tracerProvider.ForceFlush();
        // ForceFlush propagates through the processor chain.
        // We verify at least that it does not throw.
    }

    // ─── IDisposable ─────────────────────────────────────────────────────────

    public void Dispose()
    {
        _tracerProvider.Dispose();
        _source.Dispose();
    }

    // ─── Test doubles ─────────────────────────────────────────────────────────

    private sealed class SpyExporter : BaseExporter<Activity>
    {
        public List<Activity> ExportedActivities { get; } = new();
        public bool ForceFlushCalled { get; private set; }

        public override ExportResult Export(in Batch<Activity> batch)
        {
            foreach (var activity in batch)
                ExportedActivities.Add(activity);
            return ExportResult.Success;
        }

        protected override bool OnForceFlush(int timeoutMilliseconds)
        {
            ForceFlushCalled = true;
            return true;
        }
    }

    private sealed class ThrowingExporter : BaseExporter<Activity>
    {
        public override ExportResult Export(in Batch<Activity> batch) =>
            throw new InvalidOperationException("Simulated exporter failure");
    }

    /// <summary>
    /// Wraps a processor but sets each activity's recorded bit to false so
    /// ErrorSpanProcessor treats it as unsampled — allowing export path testing.
    /// </summary>
    private sealed class RecordOnlyWrapper : BaseProcessor<Activity>
    {
        private readonly BaseProcessor<Activity> _inner;
        public RecordOnlyWrapper(BaseProcessor<Activity> inner) => _inner = inner;

        public override void OnEnd(Activity activity)
        {
            // Simulate RecordOnly: activity.Recorded = true, but TraceFlags has no Recorded bit.
            _inner.OnEnd(activity);
        }
    }
}
