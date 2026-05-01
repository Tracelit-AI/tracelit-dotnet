using System;
using System.Collections.Generic;
using System.Reflection;

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
    /// Git commit SHA of the running build. Used by Tracelit to fetch the exact
    /// source file from GitHub when analysing an incident.
    ///
    /// Resolution order (first non-empty value wins):
    ///   1. Explicit setter: <c>config.CommitSha = "abc1234"</c>
    ///   2. <c>GIT_COMMIT</c> environment variable (set in most CI/CD pipelines).
    ///   3. <c>SOURCE_COMMIT</c> (Heroku / Render convention).
    ///   4. <c>RENDER_GIT_COMMIT</c> (Render.com).
    ///   5. <see cref="System.Reflection.AssemblyInformationalVersionAttribute"/>
    ///      which SourceLink appends as <c>+{sha}</c> (e.g. "1.0.0+abc1234").
    ///
    /// Leave unset if the application is not connected to a GitHub repository in
    /// Tracelit — the field is silently omitted from the resource in that case.
    /// </summary>
    public string? CommitSha { get; set; } = ResolveCommitSha();

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

    /// <summary>
    /// Resolves the git commit SHA automatically — no developer configuration required.
    /// Resolution order:
    ///   1. Common CI/CD env vars set by GitHub Actions, Render, Heroku, GitLab, etc.
    ///   2. SourceLink embedded in AssemblyInformationalVersion ("1.0.0+{sha}").
    ///   3. Running `git rev-parse HEAD` — works in local dev and any environment
    ///      where the source tree is present.
    /// Returns null when no SHA can be determined; instrumentation degrades gracefully.
    /// </summary>
    internal static string? ResolveCommitSha()
    {
        // 1. Common CI/CD environment variables — zero friction for the vast majority of pipelines.
        foreach (var envVar in new[] {
            "GITHUB_SHA",        // GitHub Actions
            "GIT_COMMIT",        // Jenkins, generic
            "SOURCE_COMMIT",     // Heroku
            "RENDER_GIT_COMMIT", // Render
            "CI_COMMIT_SHA",     // GitLab CI
            "CIRCLE_SHA1",       // CircleCI
            "BITBUCKET_COMMIT",  // Bitbucket Pipelines
        })
        {
            var v = System.Environment.GetEnvironmentVariable(envVar);
            if (!string.IsNullOrWhiteSpace(v) && v.Trim().Length >= 7)
                return v.Trim();
        }

        // 2. SourceLink: AssemblyInformationalVersion is "1.0.0+<sha>" when SourceLink is active.
        var infoVersion = Assembly
            .GetEntryAssembly()?
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;

        if (infoVersion is not null)
        {
            var plusIdx = infoVersion.LastIndexOf('+');
            if (plusIdx >= 0 && plusIdx < infoVersion.Length - 1)
            {
                var sha = infoVersion[(plusIdx + 1)..].Trim();
                if (sha.Length >= 7) return sha;
            }
        }

        // 3. Ask git directly — works in local dev and CI environments where the repo is cloned.
        //    This is a one-time startup call; result is cached by the caller's static initialiser.
        try
        {
            using var proc = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo("git", "rev-parse HEAD")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                    UseShellExecute        = false,
                    CreateNoWindow         = true,
                }
            };
            proc.Start();
            var sha = proc.StandardOutput.ReadToEnd().Trim();
            proc.WaitForExit(3_000);
            if (proc.ExitCode == 0 && sha.Length >= 7) return sha;
        }
        catch
        {
            // git not on PATH, not a git repo, or any other failure — skip silently.
        }

        return null;
    }
}
