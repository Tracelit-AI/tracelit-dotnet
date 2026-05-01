using System;
using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Tracelit.Tracing;

/// <summary>
/// MVC action + exception filter that automatically enriches ASP.NET Core
/// controller spans with OTel code-location attributes and records exceptions —
/// with zero changes required in user code.
///
/// Registered globally by <c>AddTracelit()</c> via
/// <c>services.Configure&lt;MvcOptions&gt;()</c> so it applies to every
/// controller action in the application.
///
/// What it does automatically:
/// <list type="bullet">
///   <item>
///     <c>code.function</c> — controller action method name (e.g. <c>Update</c>).
///   </item>
///   <item>
///     <c>code.namespace</c> — fully-qualified controller type
///     (e.g. <c>Products.Api.Controllers.ProductsController</c>).
///   </item>
///   <item>
///     <c>code.filepath</c> + <c>code.lineno</c> — resolved from the embedded
///     Portable PDB inside the assembly. Requires the user's project to include
///     <c>&lt;DebugType&gt;embedded&lt;/DebugType&gt;</c>. When no PDB is found
///     these attributes are omitted silently.
///   </item>
///   <item>
///     <c>exception.type</c>, <c>exception.message</c>, <c>exception.stacktrace</c>
///     — written as an OTel span event via <c>Activity.AddException</c> for any
///     exception that escapes the controller action, whether or not ASP.NET Core
///     ultimately handles it with a ProblemDetails response.
///   </item>
/// </list>
/// </summary>
internal sealed class TracelitControllerFilter : IActionFilter, IExceptionFilter
{
    // ─── IActionFilter ────────────────────────────────────────────────────────

    /// <summary>
    /// Called before the action method executes. Enriches the current
    /// <see cref="Activity"/> (the ASP.NET Core SERVER span) with code-location
    /// attributes derived from the resolved controller action descriptor.
    /// </summary>
    public void OnActionExecuting(ActionExecutingContext context)
    {
        var activity = Activity.Current;
        if (activity is null) return;

        if (context.ActionDescriptor is not ControllerActionDescriptor descriptor) return;

        var method = descriptor.MethodInfo;

        // Always available via reflection — no PDB required.
        activity.SetTag("code.function",  method.Name);
        activity.SetTag("code.namespace", method.DeclaringType?.FullName);

        // File and line from embedded PDB — degrades gracefully when unavailable.
        var (file, line) = PdbResolver.TryResolve(method);
        if (!string.IsNullOrEmpty(file))
        {
            activity.SetTag("code.filepath", file);
            if (line > 0)
                activity.SetTag("code.lineno", line);
        }
    }

    /// <summary>
    /// Called after the action method executes. When the action threw an
    /// exception that ASP.NET Core caught and turned into a response (e.g. via
    /// a global exception handler or <see cref="ProducesResponseType"/>), the
    /// span would otherwise show no error. This records the original exception.
    /// </summary>
    public void OnActionExecuted(ActionExecutedContext context)
    {
        if (context.Exception is not { } ex) return;

        var activity = Activity.Current;
        if (activity is null) return;

        // Avoid double-recording — IExceptionFilter.OnException fires first for
        // unhandled exceptions and may have already written the event.
        if (activity.Status != ActivityStatusCode.Error)
        {
            activity.AddException(ex);
            activity.SetStatus(ActivityStatusCode.Error, ex.Message);
        }
    }

    // ─── IExceptionFilter ─────────────────────────────────────────────────────

    /// <summary>
    /// Called when an exception escapes the action and has NOT yet been handled
    /// by another filter. Records the exception on the current span so that
    /// <c>exception.stacktrace</c> is always populated in Tracelit regardless
    /// of whether the exception is swallowed by a global handler.
    /// </summary>
    public void OnException(ExceptionContext context)
    {
        var activity = Activity.Current;
        if (activity is null) return;

        activity.AddException(context.Exception);
        activity.SetStatus(ActivityStatusCode.Error, context.Exception.Message);
    }
}
