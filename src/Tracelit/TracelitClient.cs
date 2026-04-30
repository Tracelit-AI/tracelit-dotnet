using System;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Tracelit.Extensions;
using Tracelit.Metrics;
using Tracelit.Tracing;

namespace Tracelit;

/// <summary>
/// Static façade for the Tracelit SDK. Use this when your application does not
/// use a dependency injection container (e.g. console apps, workers, libraries).
///
/// For ASP.NET Core applications prefer the DI extension method:
/// <c>services.AddTracelit(config => { ... })</c>.
///
/// Minimal setup:
/// <code>
/// TracelitClient.Configure(config =>
/// {
///     config.ApiKey      = "tl_live_abc123";
///     config.ServiceName = "my-worker";
///     config.Environment = "production";
/// });
/// TracelitClient.Start();
///
/// // Create a custom span
/// using var span = TracelitClient.Tracer.StartActiveSpan("process_job");
/// span.SetAttribute("job.id", jobId);
///
/// // Record a metric
/// var counter = TracelitClient.Metrics.Counter("jobs.processed");
/// counter.Add(1);
/// </code>
/// </summary>
public static class TracelitClient
{
    private static readonly object _lock = new();
    private static volatile bool _started;
    private static TracelitConfiguration _config = new();
    private static TracerProvider? _tracerProvider;
    private static MeterProvider? _meterProvider;
    private static TracelitMetrics? _metrics;
    private static ActivitySource? _activitySource;

    // ─── Configuration ───────────────────────────────────────────────────────

    /// <summary>
    /// Configures the SDK. Call this once before <see cref="Start"/>, typically
    /// at application startup.
    /// </summary>
    /// <param name="configure">Delegate to set configuration values.</param>
    /// <exception cref="InvalidOperationException">
    /// Thrown if called after <see cref="Start"/> has already been called.
    /// </exception>
    public static void Configure(Action<TracelitConfiguration> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        lock (_lock)
        {
            if (_started)
                throw new InvalidOperationException(
                    "TracelitClient.Configure must be called before Start().");
            configure(_config);
        }
    }

    // ─── Lifecycle ───────────────────────────────────────────────────────────

    /// <summary>
    /// Initialises the OpenTelemetry SDK and starts exporting telemetry.
    /// Idempotent — safe to call multiple times; only the first call takes effect.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// Thrown when the configuration is invalid (missing required fields).
    /// </exception>
    public static void Start()
    {
        if (_started) return;

        lock (_lock)
        {
            if (_started) return;

            if (!_config.Enabled)
            {
                _started = true;
                return;
            }

            _config.Validate();

            _activitySource = new ActivitySource(
                _config.ResolvedServiceName, TracelitConstants.SdkVersion);

            _metrics = new TracelitMetrics(
                _config.ResolvedServiceName, TracelitConstants.SdkVersion);

            _tracerProvider = BuildTracerProvider(_config);
            _meterProvider  = BuildMeterProvider(_config);

            _started = true;
        }
    }

    /// <summary>
    /// Shuts down the SDK, flushing all pending telemetry.
    /// Call this at application shutdown before the process exits.
    /// </summary>
    public static void Shutdown()
    {
        lock (_lock)
        {
            _tracerProvider?.Dispose();
            _meterProvider?.Dispose();
            _metrics?.Dispose();
            _activitySource?.Dispose();

            _tracerProvider = null;
            _meterProvider  = null;
            _metrics        = null;
            _activitySource = null;
            _started        = false;
            _config         = new TracelitConfiguration();
        }
    }

    // ─── Instrumentation entry points ────────────────────────────────────────

    /// <summary>
    /// Returns the <see cref="ActivitySource"/> for creating manual spans.
    /// Equivalent to Ruby's <c>Tracelit.tracer</c>.
    ///
    /// <code>
    /// using var span = TracelitClient.Tracer.StartActiveSpan("process_payment");
    /// span.SetAttribute("payment.id", id);
    /// </code>
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown if <see cref="Start"/> has not been called yet.
    /// </exception>
    public static ActivitySource Tracer
    {
        get
        {
            EnsureStarted();
            return _activitySource!;
        }
    }

    /// <summary>
    /// Returns the <see cref="TracelitMetrics"/> instance for creating manual
    /// counters, histograms, and gauges.
    /// Equivalent to Ruby's <c>Tracelit.metrics</c>.
    ///
    /// <code>
    /// var counter = TracelitClient.Metrics.Counter("orders.placed");
    /// counter.Add(1, new KeyValuePair&lt;string, object?&gt;("currency", "USD"));
    /// </code>
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown if <see cref="Start"/> has not been called yet.
    /// </exception>
    public static TracelitMetrics Metrics
    {
        get
        {
            EnsureStarted();
            return _metrics!;
        }
    }

    /// <summary>Exposes the current configuration. Useful for diagnostics.</summary>
    public static TracelitConfiguration Configuration => _config;

    // ─── Private helpers ─────────────────────────────────────────────────────

    private static void EnsureStarted()
    {
        if (!_started)
            throw new InvalidOperationException(
                "TracelitClient.Start() must be called before accessing Tracer or Metrics.");
    }

    private static TracerProvider BuildTracerProvider(TracelitConfiguration config)
    {
        var otlpOptions = new OtlpExporterOptions
        {
            Endpoint = new Uri($"{config.Endpoint.TrimEnd('/')}/v1/traces"),
            Protocol = OtlpExportProtocol.HttpProtobuf,
            Headers  = $"Authorization=Bearer {config.ApiKey}," +
                       $"X-Service-Name={config.ResolvedServiceName}," +
                       $"X-Environment={config.Environment}",
        };

        var builder = Sdk.CreateTracerProviderBuilder()
            .SetResourceBuilder(BuildResource(config))
            .AddSource(config.ResolvedServiceName)
            .AddAspNetCoreInstrumentation(opts =>
            {
                // Emit an OTel "exception" span event on every unhandled exception so
                // the Tracelit ingest pipeline can extract exception.type, exception.message,
                // and exception.stacktrace for incident grouping and AI analysis.
                opts.RecordException = true;
            })
            .AddHttpClientInstrumentation(opts => opts.RecordException = true)
            .AddSqlClientInstrumentation(opts =>
            {
                opts.RecordException = true;
            })
            .AddProcessor(_ =>
            {
                // Separate exporter instances — see ServiceCollectionExtensions for rationale.
                var batchExporter = new OtlpTraceExporter(otlpOptions);
                var errorExporter = new OtlpTraceExporter(otlpOptions);
                return new CompositeProcessor<Activity>(new BaseProcessor<Activity>[]
                {
                    new BatchActivityExportProcessor(batchExporter),
                    new ErrorSpanProcessor(errorExporter),
                });
            });

        if (config.SampleRate < 1.0)
            builder.SetSampler(new ParentBasedSampler(new ErrorAlwaysOnSampler(config.SampleRate)));

        return builder.Build()!;
    }

    private static MeterProvider BuildMeterProvider(TracelitConfiguration config)
    {
        var otlpOptions = new OtlpExporterOptions
        {
            Endpoint = new Uri($"{config.Endpoint.TrimEnd('/')}/v1/metrics"),
            Protocol = OtlpExportProtocol.HttpProtobuf,
            Headers  = $"Authorization=Bearer {config.ApiKey}," +
                       $"X-Service-Name={config.ResolvedServiceName}," +
                       $"X-Environment={config.Environment}",
        };

        return Sdk.CreateMeterProviderBuilder()
            .SetResourceBuilder(BuildResource(config))
            .AddMeter(config.ResolvedServiceName)
            .AddHttpClientInstrumentation()
            .AddRuntimeInstrumentation()
            .AddOtlpExporter((exporterOptions, readerOptions) =>
            {
                exporterOptions.Endpoint  = otlpOptions.Endpoint;
                exporterOptions.Headers   = otlpOptions.Headers;
                exporterOptions.Protocol  = otlpOptions.Protocol;

                readerOptions.TemporalityPreference = MetricReaderTemporalityPreference.Delta;

                readerOptions.PeriodicExportingMetricReaderOptions =
                    new PeriodicExportingMetricReaderOptions
                    {
                        ExportIntervalMilliseconds = 60_000,
                        ExportTimeoutMilliseconds  = 10_000,
                    };
            })
            .Build()!;
    }

    private static ResourceBuilder BuildResource(TracelitConfiguration config)
    {
        var attributes = new System.Collections.Generic.Dictionary<string, object>
        {
            ["service.name"]            = config.ResolvedServiceName,
            ["deployment.environment"]  = config.Environment,
            ["telemetry.sdk.language"]  = "dotnet",
            ["telemetry.sdk.name"]      = "tracelit",
            ["telemetry.sdk.version"]   = TracelitConstants.SdkVersion,
        };

        foreach (var kv in config.ResourceAttributes)
            attributes[kv.Key] = kv.Value;

        return ResourceBuilder.CreateEmpty().AddAttributes(attributes);
    }
}
