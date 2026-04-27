using System;
using FluentAssertions;
using Tracelit.Metrics;
using Xunit;

namespace Tracelit.Tests;

/// <summary>
/// Tests for <see cref="TracelitMetrics"/>.
/// </summary>
public sealed class MetricsTests : IDisposable
{
    private readonly TracelitMetrics _metrics;

    public MetricsTests()
    {
        _metrics = new TracelitMetrics("test-service", "0.1.0");
    }

    // ─── Counter ─────────────────────────────────────────────────────────────

    [Fact]
    public void Counter_WithName_ReturnsNonNullInstrument()
    {
        var counter = _metrics.Counter("test.counter");
        counter.Should().NotBeNull();
    }

    [Fact]
    public void Counter_WithDescriptionAndUnit_ReturnsInstrument()
    {
        var counter = _metrics.Counter(
            "orders.placed",
            description: "Total orders placed",
            unit: "{orders}");

        counter.Should().NotBeNull();
        counter.Name.Should().Be("orders.placed");
    }

    [Fact]
    public void Counter_SameName_ReturnsSameInstrument()
    {
        var c1 = _metrics.Counter("same.counter");
        var c2 = _metrics.Counter("same.counter");

        // .NET Meter returns the same instrument instance for the same name.
        c1.Should().BeSameAs(c2);
    }

    [Fact]
    public void Counter_CanAdd_WithoutThrowing()
    {
        var counter = _metrics.Counter("safe.counter");
        var act = () => counter.Add(1);
        act.Should().NotThrow();
    }

    [Fact]
    public void Counter_CanAdd_WithTagsWithoutThrowing()
    {
        var counter = _metrics.Counter("tagged.counter");
        var act = () => counter.Add(1,
            new System.Collections.Generic.KeyValuePair<string, object?>("env", "test"));
        act.Should().NotThrow();
    }

    // ─── Histogram ────────────────────────────────────────────────────────────

    [Fact]
    public void Histogram_WithName_ReturnsNonNullInstrument()
    {
        var histogram = _metrics.Histogram("test.histogram");
        histogram.Should().NotBeNull();
    }

    [Fact]
    public void Histogram_WithDescriptionAndUnit_ReturnsInstrument()
    {
        var histogram = _metrics.Histogram(
            "request.duration",
            description: "HTTP request duration",
            unit: "ms");

        histogram.Should().NotBeNull();
        histogram.Name.Should().Be("request.duration");
    }

    [Fact]
    public void Histogram_CanRecord_WithoutThrowing()
    {
        var histogram = _metrics.Histogram("safe.histogram");
        var act = () => histogram.Record(42.5);
        act.Should().NotThrow();
    }

    [Fact]
    public void Histogram_CanRecord_WithTagsWithoutThrowing()
    {
        var histogram = _metrics.Histogram("tagged.histogram");
        var act = () => histogram.Record(99.9,
            new System.Collections.Generic.KeyValuePair<string, object?>("method", "GET"));
        act.Should().NotThrow();
    }

    // ─── Gauge ────────────────────────────────────────────────────────────────

    [Fact]
    public void Gauge_WithCallback_ReturnsNonNullInstrument()
    {
        var gauge = _metrics.Gauge("test.gauge", () => 42.0);
        gauge.Should().NotBeNull();
    }

    [Fact]
    public void Gauge_WithDescriptionAndUnit_ReturnsInstrument()
    {
        var gauge = _metrics.Gauge(
            "queue.depth",
            () => 5.0,
            description: "Pending jobs",
            unit: "{jobs}");

        gauge.Should().NotBeNull();
        gauge.Name.Should().Be("queue.depth");
    }

    // ─── Dispose ─────────────────────────────────────────────────────────────

    [Fact]
    public void AfterDispose_Counter_ThrowsObjectDisposedException()
    {
        var disposedMetrics = new TracelitMetrics("disposed-service", "0.0.1");
        disposedMetrics.Dispose();

        disposedMetrics.Invoking(m => m.Counter("x"))
            .Should().Throw<ObjectDisposedException>();
    }

    [Fact]
    public void AfterDispose_Histogram_ThrowsObjectDisposedException()
    {
        var disposedMetrics = new TracelitMetrics("disposed-service", "0.0.1");
        disposedMetrics.Dispose();

        disposedMetrics.Invoking(m => m.Histogram("x"))
            .Should().Throw<ObjectDisposedException>();
    }

    [Fact]
    public void AfterDispose_Gauge_ThrowsObjectDisposedException()
    {
        var disposedMetrics = new TracelitMetrics("disposed-service", "0.0.1");
        disposedMetrics.Dispose();

        disposedMetrics.Invoking(m => m.Gauge("x", () => 0.0))
            .Should().Throw<ObjectDisposedException>();
    }

    [Fact]
    public void Dispose_CalledTwice_DoesNotThrow()
    {
        var disposedMetrics = new TracelitMetrics("idempotent-service", "0.0.1");
        disposedMetrics.Dispose();
        var act = () => disposedMetrics.Dispose();
        act.Should().NotThrow();
    }

    // ─── IDisposable ─────────────────────────────────────────────────────────

    public void Dispose() => _metrics.Dispose();
}
