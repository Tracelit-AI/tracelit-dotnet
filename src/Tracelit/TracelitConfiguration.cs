using System;
using System.Collections.Generic;

namespace Tracelit;

/// <summary>
/// Configuration for the Tracelit SDK. All options can be set programmatically
/// or via environment variables. Create via <see cref="TracelitClient.Configure"/> 
/// or pass an <see cref="Action{TracelitConfiguration}"/> to
/// <c>services.AddTracelit()</c>.
/// </summary>
public sealed class TracelitConfiguration
{
    /// <summary>
    /// Your Tracelit ingest API key. Required.
    /// Env: <c>TRACELIT_API_KEY</c>.
    /// </summary>
    public string? ApiKey { get; set; } = System.Environment.GetEnvironmentVariable("TRACELIT_API_KEY");

    /// <summary>
    /// Name of this service as it appears in Tracelit. Required.
    /// Env: <c>TRACELIT_SERVICE_NAME</c>.
    /// </summary>
    public string? ServiceName { get; set; } = System.Environment.GetEnvironmentVariable("TRACELIT_SERVICE_NAME");

    /// <summary>
    /// Deployment environment tag — e.g. "production", "staging", "development".
    /// Env: <c>TRACELIT_ENVIRONMENT</c>. Default: <c>"production"</c>.
    /// </summary>
    public string Environment { get; set; } =
        System.Environment.GetEnvironmentVariable("TRACELIT_ENVIRONMENT") ?? "production";

    /// <summary>
    /// Base URL of the Tracelit ingest API. Override only when self-hosting.
    /// Env: <c>TRACELIT_ENDPOINT</c>. Default: <c>https://ingest.tracelit.app</c>.
    /// </summary>
    public string Endpoint { get; set; } =
        System.Environment.GetEnvironmentVariable("TRACELIT_ENDPOINT") ?? "https://ingest.tracelit.app";

    /// <summary>
    /// Head-based trace sampling ratio between 0.0 and 1.0.
    /// 1.0 keeps every trace; 0.1 keeps 10%. Error spans are always exported.
    /// Env: <c>TRACELIT_SAMPLE_RATE</c>. Default: <c>1.0</c>.
    /// </summary>
    public double SampleRate { get; set; } = ParseSampleRate();

    /// <summary>
    /// Set to <c>false</c> to disable all telemetry without removing the SDK.
    /// Useful for test environments.
    /// Env: <c>TRACELIT_ENABLED</c>. Default: <c>true</c>.
    /// </summary>
    public bool Enabled { get; set; } =
        System.Environment.GetEnvironmentVariable("TRACELIT_ENABLED") != "false";

    /// <summary>
    /// Extra key/value pairs appended to every span, metric, and log record
    /// as resource attributes. Keys and values must be strings.
    /// </summary>
    public Dictionary<string, string> ResourceAttributes { get; set; } = new();

    /// <summary>
    /// Validates configuration and throws <see cref="ArgumentException"/> for
    /// any invalid or missing required values.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// Thrown when <see cref="ApiKey"/> or <see cref="ServiceName"/> is null/empty,
    /// or when <see cref="SampleRate"/> is outside [0.0, 1.0].
    /// </exception>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ApiKey))
            throw new ArgumentException("TracelitConfiguration.ApiKey is required. " +
                "Set it directly or via the TRACELIT_API_KEY environment variable.");

        if (string.IsNullOrWhiteSpace(ServiceName))
            throw new ArgumentException("TracelitConfiguration.ServiceName is required. " +
                "Set it directly or via the TRACELIT_SERVICE_NAME environment variable.");

        if (SampleRate < 0.0 || SampleRate > 1.0)
            throw new ArgumentException(
                $"TracelitConfiguration.SampleRate must be between 0.0 and 1.0, got {SampleRate}.");

        if (string.IsNullOrWhiteSpace(Endpoint))
            throw new ArgumentException("TracelitConfiguration.Endpoint must not be empty.");
    }

    /// <summary>
    /// Returns the effective service name, falling back to <c>"unknown-service"</c>
    /// if <see cref="ServiceName"/> is not set.
    /// </summary>
    public string ResolvedServiceName =>
        string.IsNullOrWhiteSpace(ServiceName) ? "unknown-service" : ServiceName!;

    private static double ParseSampleRate()
    {
        var raw = System.Environment.GetEnvironmentVariable("TRACELIT_SAMPLE_RATE");
        if (raw is not null && double.TryParse(raw, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var parsed))
            return parsed;
        return 1.0;
    }
}
