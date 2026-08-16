using System.Text.Json;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace BiatecOIDC.HealthChecks
{
    /// <summary>
    /// Renders ASP.NET Core's <see cref="HealthReport"/> as JSON for GET /health - the shape an external
    /// status page/uptime monitor can poll directly, instead of the framework default (a bare status-name
    /// plaintext body with no per-dependency detail). A separate, independently-implemented copy exists in
    /// BiatecMCP for the same reason as this repo's other small per-service helpers (AlgorandChainRegistry,
    /// NetworkResolver, ...): the two services share no compile-time coupling.
    /// </summary>
    public static class HealthCheckResponseWriter
    {
        public static Task WriteAsync(HttpContext context, HealthReport report)
        {
            context.Response.ContentType = "application/json; charset=utf-8";

            var payload = new
            {
                status = report.Status.ToString(),
                service = "BiatecOIDC",
                totalDurationMs = report.TotalDuration.TotalMilliseconds,
                checks = report.Entries.Select(entry => new
                {
                    name = entry.Key,
                    status = entry.Value.Status.ToString(),
                    description = entry.Value.Description,
                    durationMs = entry.Value.Duration.TotalMilliseconds
                })
            };

            return context.Response.WriteAsync(JsonSerializer.Serialize(payload));
        }
    }
}
