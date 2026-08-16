using System.Net;
using BiatecMCP.HealthChecks;
using BiatecMCP.Model;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace BiatecMCPTests
{
    /// <summary>
    /// Covers <see cref="OidcIssuerHealthCheck"/>'s request-shape/status-mapping logic without live network
    /// access (same "no outbound network access in this repo's build/test environment" constraint as
    /// BlockchairDataSourceTests) - a fake HttpMessageHandler stands in for the real BiatecOIDC discovery
    /// endpoint.
    /// </summary>
    [TestFixture]
    public class OidcIssuerHealthCheckTests
    {
        private sealed class FakeHttpMessageHandler : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
            public HttpRequestMessage? LastRequest { get; private set; }

            public FakeHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
            {
                _respond = respond;
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                LastRequest = request;
                return Task.FromResult(_respond(request));
            }
        }

        private sealed class FakeHttpClientFactory : IHttpClientFactory
        {
            private readonly HttpClient _client;
            public FakeHttpClientFactory(HttpClient client) => _client = client;
            public HttpClient CreateClient(string name) => _client;
        }

        private static OidcIssuerHealthCheck CreateCheck(FakeHttpMessageHandler handler, string issuer = "https://oidc.biatec.io")
        {
            var httpClient = new HttpClient(handler);
            var factory = new FakeHttpClientFactory(httpClient);
            var options = Options.Create(new OidcConfiguration { Issuer = issuer });
            return new OidcIssuerHealthCheck(factory, options);
        }

        [Test]
        public async Task CheckHealthAsync_DiscoveryReturnsOk_ReturnsHealthyAndRequestsWellKnownPath()
        {
            var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
            var check = CreateCheck(handler, "https://oidc.biatec.io");

            var result = await check.CheckHealthAsync(new HealthCheckContext());

            Assert.That(result.Status, Is.EqualTo(HealthStatus.Healthy));
            Assert.That(handler.LastRequest!.RequestUri!.ToString(), Is.EqualTo("https://oidc.biatec.io/.well-known/openid-configuration"));
        }

        [Test]
        public async Task CheckHealthAsync_DiscoveryReturnsServerError_ReturnsUnhealthy()
        {
            var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            var check = CreateCheck(handler);

            var result = await check.CheckHealthAsync(new HealthCheckContext());

            Assert.That(result.Status, Is.EqualTo(HealthStatus.Unhealthy));
        }

        [Test]
        public async Task CheckHealthAsync_HttpClientThrows_ReturnsUnhealthy()
        {
            var handler = new FakeHttpMessageHandler(_ => throw new HttpRequestException("connection refused"));
            var check = CreateCheck(handler);

            var result = await check.CheckHealthAsync(new HealthCheckContext());

            Assert.That(result.Status, Is.EqualTo(HealthStatus.Unhealthy));
        }
    }
}
