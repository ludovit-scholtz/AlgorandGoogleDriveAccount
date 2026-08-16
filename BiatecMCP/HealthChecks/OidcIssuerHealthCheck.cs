using BiatecMCP.Model;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace BiatecMCP.HealthChecks
{
    /// <summary>
    /// Confirms BiatecOIDC's discovery document is reachable. BiatecMCP holds no key material and no
    /// session state of its own (see CLAUDE.md's "MCP server" note) - every tool call ultimately depends on
    /// BiatecOIDC being up, so that reachability is the one dependency worth reporting on a status page.
    /// Reported Unhealthy (not Degraded) on failure since it genuinely means every sign/build/broadcast
    /// tool is unusable, not just running slower.
    /// </summary>
    public class OidcIssuerHealthCheck : IHealthCheck
    {
        private readonly HttpClient _httpClient;
        private readonly OidcConfiguration _oidcConfig;

        public OidcIssuerHealthCheck(IHttpClientFactory httpClientFactory, IOptions<OidcConfiguration> oidcConfig)
        {
            _httpClient = httpClientFactory.CreateClient(nameof(OidcIssuerHealthCheck));
            _oidcConfig = oidcConfig.Value;
        }

        public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
        {
            try
            {
                var discoveryUrl = _oidcConfig.Issuer.TrimEnd('/') + "/.well-known/openid-configuration";
                using var response = await _httpClient.GetAsync(discoveryUrl, cancellationToken);
                return response.IsSuccessStatusCode
                    ? HealthCheckResult.Healthy($"BiatecOIDC discovery reachable ({(int)response.StatusCode}).")
                    : HealthCheckResult.Unhealthy($"BiatecOIDC discovery returned {(int)response.StatusCode}.");
            }
            catch (Exception ex)
            {
                return HealthCheckResult.Unhealthy("BiatecOIDC discovery unreachable.", ex);
            }
        }
    }
}
