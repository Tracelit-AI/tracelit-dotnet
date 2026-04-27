using System;
using System.Collections.Generic;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Tracelit.Metrics;
using Tracelit.Tracing;

namespace Tracelit.Extensions;

/// <summary>
/// Extension methods for registering the Tracelit SDK with the .NET
/// dependency injection container.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers and configures the Tracelit observability SDK.
    ///
    /// This method sets up:
    /// <list type="bullet">
    ///   <item>OTLP trace export with <see cref="ErrorAlwaysOnSampler"/> + <see cref="ErrorSpanProcessor"/>.</item>
    ///   <item>OTLP log export correlated to the active trace via <c>ILoggingBuilder</c>.</item>
    ///   <item>OTLP metrics export with a 60-second periodic reader.</item>
    ///   <item><see cref="MemoryPollerService"/> as a hosted background service.</item>
    /// </list>
    ///
    /// Example:
    /// <code>
    /// builder.Services.AddTracelit(config =>
    /// {
    ///     config.ApiKey      = "tl_live_abc123";
    ///     config.ServiceName = "payments-api";
    ///     config.Environment = "production";
    ///     config.SampleRate  = 0.1;
    /// });
    /// </code>
    /// </summary>
    /// <param name="services">The service collection to add Tracelit to.</param>
    /// <param name="configure">Delegate to configure <see cref="TracelitConfiguration"/>.</param>
    /// <returns>The same <paramref name="services"/> for fluent chaining.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="services"/> or <paramref name="configure"/> is null.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Thrown when the resulting configuration fails validation (missing API key, etc.).
    /// </exception>
    public static IServiceCollection AddTracelit(
        this IServiceCollection services,
        Action<TracelitConfiguration> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        var config = new TracelitConfiguration();
        configure(config);

        if (!config.Enabled)
            return services;

        config.Validate();

        var resource = BuildResource(config);

        // --- Tracing ---
        ConfigureTracing(services, config, resource);

        // --- Metrics ---
        ConfigureMetrics(services, config, resource);

        // --- Logging ---
        ConfigureLogging(services, config);

        // --- Memory poller ---
        services.AddSingleton(sp => new TracelitMetrics(
            config.ResolvedServiceName, TracelitConstants.SdkVersion));
        services.AddHostedService<MemoryPollerService>();

        // Expose config for static façade and diagnostics.
        services.AddSingleton(config);

        return services;
    }

    // ─── Private helpers ─────────────────────────────────────────────────────

    private static ResourceBuilder BuildResource(TracelitConfiguration config)
    {
        var attributes = new Dictionary<string, object>
        {
            ["service.name"]            = config.ResolvedServiceName,
            ["deployment.environment"]  = config.Environment,
            ["telemetry.sdk.language"]  = "dotnet",
            ["telemetry.sdk.name"]      = "tracelit",
            ["telemetry.sdk.version"]   = TracelitConstants.SdkVersion,
        };

        foreach (var kv in config.ResourceAttributes)
            attributes[kv.Key] = kv.Value;

        return ResourceBuilder
            .CreateEmpty()
            .AddAttributes(attributes);
    }

    private static void ConfigureTracing(
        IServiceCollection services,
        TracelitConfiguration config,
        ResourceBuilder resource)
    {
        var otlpOptions = BuildOtlpOptions(config, "/v1/traces");

        services.AddOpenTelemetry()
            .WithTracing(builder =>
            {
                builder
                    .SetResourceBuilder(resource)
                    .AddSource(config.ResolvedServiceName)
                    .AddAspNetCoreInstrumentation(opts =>
                    {
                        opts.RecordException = true;
                    })
                    .AddHttpClientInstrumentation(opts =>
                    {
                        opts.RecordException = true;
                    })
                    .AddSqlClientInstrumentation(opts =>
                    {
                        opts.RecordException = true;
                    })
                    .AddProcessor(sp =>
                    {
                        // Each processor gets its own exporter instance.
                        //
                        // Sharing a single OtlpTraceExporter between BatchActivityExportProcessor
                        // (background-thread export) and ErrorSpanProcessor (inline request-thread
                        // export) is unsafe: BatchActivityExportProcessor takes ownership and calls
                        // Shutdown/Dispose on the exporter when the tracer provider tears down,
                        // leaving ErrorSpanProcessor with a dead reference. Concurrent Export()
                        // calls on the same instance also risk race conditions inside the exporter.
                        var batchExporter = new OtlpTraceExporter(otlpOptions);
                        var errorExporter = new OtlpTraceExporter(otlpOptions);

                        return new CompositeProcessor<Activity>(new BaseProcessor<Activity>[]
                        {
                            new BatchActivityExportProcessor(batchExporter),
                            new ErrorSpanProcessor(errorExporter),
                        });
                    });

                // Apply the custom sampler only when rate < 1.0.
                // At 1.0 the default AlwaysOn sampler is correct and more efficient.
                if (config.SampleRate < 1.0)
                {
                    builder.SetSampler(new ParentBasedSampler(
                        new ErrorAlwaysOnSampler(config.SampleRate)));
                }
            });
    }

    private static void ConfigureMetrics(
        IServiceCollection services,
        TracelitConfiguration config,
        ResourceBuilder resource)
    {
        var otlpOptions = BuildOtlpOptions(config, "/v1/metrics");

        services.AddOpenTelemetry()
            .WithMetrics(builder =>
            {
                builder
                    .SetResourceBuilder(resource)
                    .AddMeter(config.ResolvedServiceName)
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation()
                    .AddOtlpExporter((exporterOptions, readerOptions) =>
                    {
                        exporterOptions.Endpoint  = otlpOptions.Endpoint;
                        exporterOptions.Headers   = otlpOptions.Headers;
                        exporterOptions.Protocol  = otlpOptions.Protocol;

                        // Export every 60 seconds with a 10 second timeout,
                        // matching the Ruby SDK's PeriodicMetricReader defaults.
                        readerOptions.PeriodicExportingMetricReaderOptions =
                            new PeriodicExportingMetricReaderOptions
                            {
                                ExportIntervalMilliseconds = 60_000,
                                ExportTimeoutMilliseconds  = 10_000,
                            };

                        // Delta temporality: emit the delta since the last export
                        // rather than a cumulative sum. Matches the Ruby SDK behaviour
                        // (OTEL_EXPORTER_OTLP_METRICS_TEMPORALITY_PREFERENCE=delta).
                        readerOptions.TemporalityPreference =
                            MetricReaderTemporalityPreference.Delta;
                    });
            });
    }

    private static void ConfigureLogging(
        IServiceCollection services,
        TracelitConfiguration config)
    {
        var otlpOptions = BuildOtlpOptions(config, "/v1/logs");

        services.AddLogging(logging =>
        {
            logging.AddOpenTelemetry(otelLogging =>
            {
                otelLogging.SetResourceBuilder(BuildResource(config));
                // Attach trace_id + span_id to every log record automatically.
                otelLogging.IncludeFormattedMessage = true;
                otelLogging.IncludeScopes = true;
                otelLogging.AddOtlpExporter(opts =>
                {
                    opts.Endpoint = otlpOptions.Endpoint;
                    opts.Headers  = otlpOptions.Headers;
                    opts.Protocol = otlpOptions.Protocol;
                });
            });
        });
    }

    private static OtlpExporterOptions BuildOtlpOptions(
        TracelitConfiguration config,
        string path)
    {
        var baseEndpoint = config.Endpoint.TrimEnd('/');
        return new OtlpExporterOptions
        {
            Endpoint = new Uri($"{baseEndpoint}{path}"),
            Protocol = OtlpExportProtocol.HttpProtobuf,
            Headers  = $"Authorization=Bearer {config.ApiKey}," +
                       $"X-Service-Name={config.ResolvedServiceName}," +
                       $"X-Environment={config.Environment}",
        };
    }
}
