using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using OpenTelemetry.Trace;

namespace Tracelit.Tracing;

/// <summary>
/// Extension and helper methods that make manual span instrumentation easier
/// while automatically tagging spans with OTel code-location attributes
/// (<c>code.filepath</c>, <c>code.lineno</c>, <c>code.function</c>,
/// <c>code.namespace</c>).
///
/// These attributes are used by the Tracelit server to fetch the relevant
/// source file from GitHub at the exact commit SHA that was running when an
/// incident occurred, producing accurate Problem Detail and Recommendation
/// sections instead of AI-hallucinated code.
/// </summary>
public static class ActivityExtensions
{
    // ─── Span creation ───────────────────────────────────────────────────────

    /// <summary>
    /// Starts a new activity tagged with the caller's file path, line number,
    /// function name, and declaring type namespace. Caller attributes are
    /// resolved at compile time via <c>[CallerFilePath]</c> etc., so there is
    /// no runtime reflection overhead.
    ///
    /// The <c>PathMap</c> MSBuild property in <c>Tracelit.csproj</c> ensures
    /// <c>[CallerFilePath]</c> bakes in a repo-relative path (e.g.
    /// <c>Controllers/ProductsController.cs</c>) rather than an absolute
    /// build-machine path.
    ///
    /// Usage — identical call site to <c>ActivitySource.StartActivity</c>:
    /// <code>
    /// using var span = tracer.StartSpan("products.update");
    /// span?.SetTag("product.id", id);
    /// </code>
    /// </summary>
    public static Activity? StartSpan(
        this ActivitySource source,
        string name,
        ActivityKind kind = ActivityKind.Internal,
        [CallerFilePath]   string callerFile   = "",
        [CallerLineNumber] int    callerLine   = 0,
        [CallerMemberName] string callerMember = "")
    {
        var activity = source.StartActivity(name, kind);
        if (activity is null)
            return null;

        if (!string.IsNullOrEmpty(callerFile))
        {
            // Normalise path separators to forward slashes so the value is
            // consistent across Windows and Unix build environments.
            activity.SetTag("code.filepath", callerFile.Replace('\\', '/'));
        }
        if (callerLine > 0)
            activity.SetTag("code.lineno", callerLine);
        if (!string.IsNullOrEmpty(callerMember))
            activity.SetTag("code.function", callerMember);

        return activity;
    }

    // ─── Error recording ─────────────────────────────────────────────────────

    /// <summary>
    /// Records an exception on the activity and marks the span as errored.
    ///
    /// This is the correct way to surface a *caught* exception to Tracelit.
    /// <see cref="OpenTelemetry.Trace.ActivityExtensions.RecordException"/> writes the
    /// <c>exception</c> span event with <c>exception.type</c>,
    /// <c>exception.message</c>, and <c>exception.stacktrace</c> — exactly
    /// what the Tracelit ingest pipeline needs to populate the Stack Trace
    /// panel and fingerprint the incident.
    ///
    /// Without this call, caught exceptions are silently swallowed and
    /// <c>exception_stacktrace</c> is empty in the incident.
    ///
    /// Usage:
    /// <code>
    /// catch (Exception ex)
    /// {
    ///     span?.Fail(ex);
    ///     throw; // or return error response
    /// }
    /// </code>
    /// </summary>
    public static void Fail(this Activity? activity, Exception exception)
    {
        if (activity is null || exception is null)
            return;

        activity.AddException(exception);
        activity.SetStatus(ActivityStatusCode.Error, exception.Message);
    }

    /// <summary>
    /// Marks the activity as errored with a plain message (no exception object).
    /// Use <see cref="Fail(Activity?, Exception)"/> instead when an exception is available.
    /// </summary>
    public static void Fail(this Activity? activity, string message)
    {
        if (activity is null)
            return;

        activity.SetStatus(ActivityStatusCode.Error, message);
    }
}
