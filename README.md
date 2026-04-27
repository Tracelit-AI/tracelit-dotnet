# Tracelit .NET SDK

Official .NET SDK for [Tracelit](https://tracelit.io) — drop-in OpenTelemetry instrumentation for ASP.NET Core and .NET worker apps. Sends traces, metrics, and logs to the Tracelit ingest API via OTLP/HTTP.

**Requirements:** .NET 8+

---

## Installation

```bash
dotnet add package Tracelit
```

---

## Setup

### ASP.NET Core (recommended)

In `Program.cs`:

```csharp
builder.Services.AddTracelit(config =>
{
    config.ApiKey      = Environment.GetEnvironmentVariable("TRACELIT_API_KEY");
    config.ServiceName = "payments-api";
    config.Environment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "production";
    config.SampleRate  = 1.0;
});
```

That is all. The SDK wires up traces, logs, and metrics automatically.

### Console / Worker apps (non-DI)

```csharp
TracelitClient.Configure(config =>
{
    config.ApiKey      = "tl_live_abc123";
    config.ServiceName = "my-worker";
    config.Environment = "production";
});
TracelitClient.Start();

// At application exit
TracelitClient.Shutdown();
```

---

## Configuration reference

| Option | Env variable | Default | Description |
|---|---|---|---|
| `ApiKey` | `TRACELIT_API_KEY` | `null` | **Required.** Your Tracelit ingest API key. |
| `ServiceName` | `TRACELIT_SERVICE_NAME` | `null` | **Required.** Name of this service in Tracelit. |
| `Environment` | `TRACELIT_ENVIRONMENT` | `"production"` | Deployment environment tag. |
| `Endpoint` | `TRACELIT_ENDPOINT` | `https://ingest.tracelit.app` | Base URL of the Tracelit ingest API. Override only when self-hosting. |
| `SampleRate` | `TRACELIT_SAMPLE_RATE` | `1.0` | Head-based trace sampling ratio (0.0–1.0). **Errors are always exported.** |
| `Enabled` | `TRACELIT_ENABLED` | `true` | Set to `false` to disable all telemetry without removing the SDK. |
| `ResourceAttributes` | — | `{}` | Extra `Dictionary<string, string>` appended to every span, metric, and log. |

### Custom resource attributes

```csharp
builder.Services.AddTracelit(config =>
{
    config.ApiKey      = "tl_live_abc123";
    config.ServiceName = "orders-api";
    config.ResourceAttributes = new()
    {
        ["deployment.region"] = "us-east-1",
        ["team"]              = "platform",
    };
});
```

---

## Manual trace instrumentation

### ASP.NET Core (DI)

Inject `System.Diagnostics.ActivitySource` via the service name or use the static façade:

```csharp
// Static façade (works in both DI and non-DI apps after Start() is called)
using var span = TracelitClient.Tracer.StartActiveSpan("process_payment");
span?.SetTag("payment.id", payment.Id.ToString());
span?.SetTag("payment.amount", amount);

var result = ProcessPayment(payment);

span?.SetTag("payment.status", result.Status);
```

---

## Manual metrics instrumentation

### Counter

```csharp
var counter = TracelitClient.Metrics.Counter(
    "orders.placed",
    description: "Total orders placed",
    unit: "{orders}");

counter.Add(1,
    new KeyValuePair<string, object?>("currency", "USD"),
    new KeyValuePair<string, object?>("channel", "web"));
```

### Histogram

```csharp
var histogram = TracelitClient.Metrics.Histogram(
    "external.api.duration",
    description: "External API call duration",
    unit: "ms");

var sw = Stopwatch.StartNew();
await CallExternalApiAsync();
histogram.Record(sw.Elapsed.TotalMilliseconds,
    new KeyValuePair<string, object?>("service", "stripe"));
```

### Gauge

```csharp
var gauge = TracelitClient.Metrics.Gauge(
    "job_queue.depth",
    () => (double)JobQueue.PendingCount,
    description: "Number of pending background jobs",
    unit: "{jobs}");
```

---

## Automatic instrumentation

The SDK instruments the following automatically with no code changes:

| Library | What is captured |
|---|---|
| ASP.NET Core | HTTP request traces, request duration/count/error metrics |
| HttpClient | Outbound HTTP call traces |
| SqlClient | SQL query traces |
| .NET runtime | GC, thread pool, lock contention metrics |

---

## Automatic metrics collection

| Metric | Type | Description |
|---|---|---|
| `http.server.request.duration` | Histogram | HTTP request duration |
| `http.server.active_requests` | UpDownCounter | In-flight requests |
| `http.client.request.duration` | Histogram | Outbound HTTP duration |
| `dotnet.gc.*` | Various | GC collections, heap size, pause time |
| `dotnet.thread_pool.*` | Various | Thread pool queue length, worker threads |
| `process.memory.rss` | Gauge | Process working set in MB (polled every 60s) |

---

## Log forwarding

When using `AddTracelit()`, all `ILogger` output is forwarded to the Tracelit logs table via OTLP. Log records are automatically correlated to the active span (`trace_id` + `span_id`).

```csharp
// This log automatically includes trace_id/span_id from the current activity
_logger.LogInformation("Order {OrderId} processed in {ElapsedMs}ms", orderId, elapsed);
```

---

## Sampling and error guarantee

```csharp
config.SampleRate = 0.1; // keep 10% of traces
```

**Error spans are always exported**, even when the parent trace falls outside the sample ratio. The SDK uses a custom `ErrorAlwaysOnSampler` + `ErrorSpanProcessor` pair to guarantee this with no extra configuration.

---

## Disabling in tests

```csharp
// In test setup or appsettings.Testing.json
config.Enabled = false;
// or: TRACELIT_ENABLED=false
```

---

## Running the SDK's own tests

```bash
dotnet test
```

---

## Releasing

Use the included helper script to cut a release locally:

```bash
./release.sh patch          # x.y.Z+1
./release.sh minor          # x.Y+1.0
./release.sh major          # X+1.0.0
./release.sh 1.2.3          # explicit version
./release.sh patch --dry-run
```

The script bumps `<Version>` in `src/Tracelit/Tracelit.csproj`, commits the change, pushes it to `main`, then creates and pushes an annotated tag. Pushing the tag triggers the [release workflow](.github/workflows/release.yml), which runs tests, packs and publishes to NuGet, and creates a GitHub Release with an auto-generated CHANGELOG entry.

See [CHANGELOG.md](CHANGELOG.md) for the release history.

---

## Design specification

The prompts and design notes used to generate this SDK are in [llm_prompt.txt](llm_prompt.txt).

---

## Project structure

```
src/Tracelit/
├── TracelitClient.cs               Static façade for non-DI usage
├── TracelitConfiguration.cs        Config with env var defaults + Validate()
├── TracelitConstants.cs            SDK version constant
├── AssemblyInfo.cs                 InternalsVisibleTo for test project
├── Tracing/
│   ├── ErrorAlwaysOnSampler.cs     Upgrades Drop→RecordOnly for error capture
│   └── ErrorSpanProcessor.cs       Force-exports unsampled error spans
├── Metrics/
│   ├── TracelitMetrics.cs          Counter/Histogram/Gauge wrappers
│   └── MemoryPollerService.cs      Background process.memory.rss polling
└── Extensions/
    └── ServiceCollectionExtensions.cs  AddTracelit() DI integration

tests/Tracelit.Tests/
├── ConfigurationTests.cs           Validate(), defaults, edge cases
├── ErrorAlwaysOnSamplerTests.cs    Drop→RecordOnly, description
├── ErrorSpanProcessorTests.cs      OK/sampled/unsampled/error paths
├── TracelitClientTests.cs          Configure/Start/Shutdown lifecycle
└── MetricsTests.cs                 Counter/Histogram/Gauge, dispose safety
```
