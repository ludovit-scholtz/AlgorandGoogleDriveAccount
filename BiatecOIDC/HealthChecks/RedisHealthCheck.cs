using Microsoft.Extensions.Diagnostics.HealthChecks;
using StackExchange.Redis;

namespace BiatecOIDC.HealthChecks
{
    /// <summary>
    /// Confirms Redis (the OIDC code/token store - see JwtIssuerService's GETDEL-based one-time-use
    /// records) is actually reachable, not just that the process is up - a status page/uptime monitor
    /// polling GET /health cares about this because a Redis outage breaks /authorize and /token outright.
    /// </summary>
    public class RedisHealthCheck : IHealthCheck
    {
        private readonly IConnectionMultiplexer _connectionMultiplexer;

        public RedisHealthCheck(IConnectionMultiplexer connectionMultiplexer)
        {
            _connectionMultiplexer = connectionMultiplexer;
        }

        public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
        {
            try
            {
                var latency = await _connectionMultiplexer.GetDatabase().PingAsync().WaitAsync(cancellationToken);
                return HealthCheckResult.Healthy($"Redis responded in {latency.TotalMilliseconds:F0}ms.");
            }
            catch (Exception ex)
            {
                return HealthCheckResult.Unhealthy("Redis ping failed.", ex);
            }
        }
    }
}
