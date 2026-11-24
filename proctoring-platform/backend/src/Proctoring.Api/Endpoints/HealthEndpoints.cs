using System.Text.Json;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Proctoring.Api.Endpoints;

public static class HealthEndpoints
{
    /// <summary>
    /// /health/live: the process is running (no dependencies checked; used to restart a hung container).
    /// /health/ready: dependencies are reachable (database now; Redis is added in Phase 3).
    /// </summary>
    public static void MapHealthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false, ResponseWriter = WriteJson })
            .WithTags("Health");
        app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = c => c.Tags.Contains("ready"), ResponseWriter = WriteJson })
            .WithTags("Health");
    }

    private static Task WriteJson(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json";
        var body = new
        {
            status = report.Status.ToString().ToLowerInvariant(),
            totalDurationMs = Math.Round(report.TotalDuration.TotalMilliseconds, 1),
            checks = report.Entries.Select(e => new
            {
                name = e.Key,
                status = e.Value.Status.ToString().ToLowerInvariant(),
                durationMs = Math.Round(e.Value.Duration.TotalMilliseconds, 1),
                // Exception messages can contain connection strings: report only the check's own description.
                description = e.Value.Description,
            }),
        };
        return context.Response.WriteAsync(JsonSerializer.Serialize(body, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }
}
