using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace BiatecMCPTests
{
    /// <summary>
    /// TDD spike confirming BiatecMCP's OAuth 2.1 resource-server wiring (Program.cs: AddJwtBearer +
    /// AddMcp) actually produces spec-correct behavior end to end: an unauthenticated MCP request gets a
    /// 401 with a WWW-Authenticate challenge pointing at the RFC 9728 Protected Resource Metadata
    /// document, and that document itself is well-formed and served anonymously. This was the first test
    /// written for the BiatecMCP OAuth rewrite - it exists specifically to pin down the exact SDK wiring
    /// (ModelContextProtocol.AspNetCore 1.4.1's AddMcp/AddAuthorizationFilters) empirically, since the
    /// package has no first-party usage examples in this repo to copy from.
    /// </summary>
    [TestFixture]
    public class McpAuthEndpointTests
    {
        private WebApplicationFactory<BiatecMCP.Program> _factory = null!;

        [SetUp]
        public void SetUp()
        {
            _factory = new WebApplicationFactory<BiatecMCP.Program>();
        }

        [TearDown]
        public void TearDown()
        {
            _factory.Dispose();
        }

        [Test]
        public async Task ProtectedResourceMetadata_IsServedAnonymously_AndDescribesThisResourceAndBiatecOidc()
        {
            var client = _factory.CreateClient();
            client.DefaultRequestHeaders.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
            client.DefaultRequestHeaders.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("text/event-stream"));

            var response = await client.GetAsync("/.well-known/oauth-protected-resource");

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            var body = await response.Content.ReadAsStringAsync();
            using var json = JsonDocument.Parse(body);
            Assert.That(json.RootElement.GetProperty("resource").GetString(), Does.Contain("/mcp"));
            var authServers = json.RootElement.GetProperty("authorization_servers").EnumerateArray().Select(e => e.GetString()).ToList();
            Assert.That(authServers, Has.Some.Contains("oidc.biatec.io"));
        }

        [Test]
        public async Task McpEndpoint_NoBearerToken_Returns401WithWwwAuthenticateChallenge()
        {
            var client = _factory.CreateClient();
            client.DefaultRequestHeaders.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
            client.DefaultRequestHeaders.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("text/event-stream"));

            var response = await client.PostAsync("/mcp", JsonContent(InitializeRequestJson));

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
            Assert.That(response.Headers.WwwAuthenticate, Is.Not.Empty);
            var challenge = response.Headers.WwwAuthenticate.First();
            Assert.That(challenge.Scheme, Is.EqualTo("Bearer"));
            Assert.That(challenge.Parameter, Does.Contain("resource_metadata="));
            Assert.That(challenge.Parameter, Does.Contain("oauth-protected-resource"));
        }

        [Test]
        public async Task McpEndpoint_InvalidBearerToken_Returns401()
        {
            var client = _factory.CreateClient();
            client.DefaultRequestHeaders.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
            client.DefaultRequestHeaders.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("text/event-stream"));
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "not-a-real-jwt");

            var response = await client.PostAsync("/mcp", JsonContent(InitializeRequestJson));

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        }

        private const string InitializeRequestJson = """
            {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"test","version":"1.0"}}}
            """;

        private static StringContent JsonContent(string json) => new(json, System.Text.Encoding.UTF8, "application/json");
    }
}
