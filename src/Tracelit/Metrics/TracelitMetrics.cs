using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;

namespace Tracelit.Metrics;

/// <summary>
/// Wraps a <see cref="System.Diagnostics.Metrics.Meter"/> to expose strongly typed
/// Counter, Histogram, and Gauge factories for manual instrumentation.
///
/// Usage after the SDK is initialised:
/// <code>
/// var counter = TracelitClient.Metrics.Counter("orders.placed", description: "...", unit: "{orders}");
/// counter.Add(1, new KeyValuePair&lt;string, object?&gt;("currency", "USD"));
/// </code>
/// </summary>
public sealed class TracelitMetrics : IDisposable
{
    private readonly Meter _meter;
    private bool _disposed;

    internal TracelitMetrics(string serviceName, string version)
    {
        _meter = new Meter(serviceName, version);
    }

    /// <summary>
    /// Creates a monotonically increasing counter instrument.
    /// Equivalent to Ruby's <c>Tracelit::Metrics.counter(...)</c>.
    /// </summary>
    /// <param name="name">Instrument name, e.g. <c>"http.server.request.count"</c>.</param>
    /// <param name="description">Human-readable description shown in Tracelit.</param>
    /// <param name="unit">UCUM unit string, e.g. <c>"{requests}"</c> or <c>"ms"</c>.</param>
    public Counter<long> Counter(string name, string description = "", string unit = "")
    {
        ThrowIfDisposed();
        return _meter.CreateCounter<long>(name, unit: unit, description: description);
    }

    /// <summary>
    /// Creates a histogram instrument for recording distributions of values such as
    /// durations or sizes.
    /// Equivalent to Ruby's <c>Tracelit::Metrics.histogram(...)</c>.
    /// </summary>
    public Histogram<double> Histogram(string name, string description = "", string unit = "")
    {
        ThrowIfDisposed();
        return _meter.CreateHistogram<double>(name, unit: unit, description: description);
    }

    /// <summary>
    /// Creates an observable up-down gauge instrument that invokes <paramref name="observeValue"/>
    /// on each collection cycle to record a current level (queue depth, pool size, etc.).
    /// Equivalent to Ruby's <c>Tracelit::Metrics.gauge(...)</c>.
    /// </summary>
    /// <param name="name">Instrument name, e.g. <c>"process.memory.rss"</c>.</param>
    /// <param name="observeValue">
    /// Callback invoked each metric export cycle; should return the current measured value.
    /// </param>
    /// <param name="description">Human-readable description.</param>
    /// <param name="unit">UCUM unit string, e.g. <c>"MB"</c>.</param>
    public ObservableGauge<double> Gauge(
        string name,
        Func<double> observeValue,
        string description = "",
        string unit = "")
    {
        ThrowIfDisposed();
        return _meter.CreateObservableGauge(name, observeValue, unit: unit, description: description);
    }

    /// <summary>
    /// Creates a gauge with multiple observable measurements (tag sets).
    /// </summary>
    public ObservableGauge<double> Gauge(
        string name,
        Func<IEnumerable<Measurement<double>>> observeValues,
        string description = "",
        string unit = "")
    {
        ThrowIfDisposed();
        return _meter.CreateObservableGauge(name, observeValues, unit: unit, description: description);
    }

    /// <summary>Exposes the underlying <see cref="Meter"/> for advanced scenarios.</summary>
    internal Meter UnderlyingMeter => _meter;

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _meter.Dispose();
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(TracelitMetrics));
    }
}
