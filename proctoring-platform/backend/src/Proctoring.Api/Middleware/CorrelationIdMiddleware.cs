using System.Diagnostics;
using System.Text.RegularExpressions;
using Proctoring.Contracts.Errors;

namespace Proctoring.Api.Middleware;

/// <summary>
/// Gives every request a correlation ID: the caller's X-Correlation-ID if it is well-formed, otherwise a
/// new one. It is returned in the response header, added to every log line of the request (log scope)
/// and included in error responses, so one ID ties together browser, backend and (later) AI-service logs.
/// Also logs one line per request: method, path, status and duration - never headers or bodies, so
/// tokens and audio never reach the logs.
/// </summary>
public sealed partial class CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger)
{
    public const string ItemKey = "CorrelationId";

    [GeneratedRegex("^[A-Za-z0-9._-]{1,64}$")]
    private static partial Regex ValidId();

    public async Task InvokeAsync(HttpContext context)
    {
        var incoming = context.Request.Headers[ApiHeaders.CorrelationId].ToString();
        var correlationId = ValidId().IsMatch(incoming) ? incoming : Guid.NewGuid().ToString("N");
        context.Items[ItemKey] = correlationId;
        // Set when the response starts: the exception handler clears headers before writing an error,
        // and error responses need the ID the most.
        context.Response.OnStarting(() =>
        {
            context.Response.Headers[ApiHeaders.CorrelationId] = correlationId;
            return Task.CompletedTask;
        });

        using var scope = logger.BeginScope(new Dictionary<string, object>
        {
            ["CorrelationId"] = correlationId,
            ["TraceId"] = Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier,
        });
        var started = Stopwatch.GetTimestamp();
        try
        {
            await next(context);
        }
        finally
        {
            var endpoint = context.GetEndpoint() as RouteEndpoint;
            logger.LogInformation(
                "HTTP {Method} {Route} responded {StatusCode} in {ElapsedMs:0.0} ms",
                context.Request.Method,
                endpoint?.RoutePattern.RawText ?? context.Request.Path.Value,
                context.Response.StatusCode,
                Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
    }

    public static string? Get(HttpContext context) => context.Items[ItemKey] as string;
}
