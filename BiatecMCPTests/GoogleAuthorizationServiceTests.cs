using BiatecMCP.BusinessLogic;
using BiatecMCP.Model;
using BiatecSelfCustodyCore.Model;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Moq.Protected;
using System.Net;
using System.Text;
using System.Text.Json;

namespace BiatecMCPTests
{
    /// <summary>
    /// Covers F-10: <see cref="GoogleAuthorizationService.HasScopeAsync"/> must actually check the token's
    /// granted scopes (via Google's tokeninfo endpoint) rather than treating "a token exists" as "the
    /// requested scope is granted".
    /// </summary>
    [TestFixture]
    public class GoogleAuthorizationServiceTests
    {
        private Mock<IDistributedCache> _mockCache = null!;
        private Mock<ILogger<GoogleAuthorizationService>> _mockLogger = null!;
        private Mock<IHttpContextAccessor> _mockHttpContextAccessor = null!;
        private Mock<IOptionsMonitor<Configuration>> _mockConfig = null!;
        private Mock<HttpMessageHandler> _mockHandler = null!;
        private GoogleAuthorizationService _service = null!;

        private const string SessionId = "test-session";
        private const string AccessToken = "test-access-token";

        [SetUp]
        public void SetUp()
        {
            _mockCache = new Mock<IDistributedCache>();
            _mockLogger = new Mock<ILogger<GoogleAuthorizationService>>();
            _mockHttpContextAccessor = new Mock<IHttpContextAccessor>();
            _mockConfig = new Mock<IOptionsMonitor<Configuration>>();
            _mockConfig.Setup(c => c.CurrentValue).Returns(new Configuration());
            _mockHandler = new Mock<HttpMessageHandler>();

            var httpClient = new HttpClient(_mockHandler.Object);
            var mockFactory = new Mock<IHttpClientFactory>();
            mockFactory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(httpClient);

            _service = new GoogleAuthorizationService(
                _mockCache.Object,
                _mockLogger.Object,
                _mockHttpContextAccessor.Object,
                _mockConfig.Object,
                mockFactory.Object);

            var deviceInfoJson = JsonSerializer.Serialize(new PairedDeviceInfo
            {
                AccessToken = AccessToken,
                Email = "user@example.com",
                ExpiresAt = DateTime.UtcNow.AddHours(1)
            });
            _mockCache.Setup(c => c.GetAsync($"device_session:{SessionId}", It.IsAny<CancellationToken>()))
                .ReturnsAsync(Encoding.UTF8.GetBytes(deviceInfoJson));
        }

        private void SetupTokenInfoResponse(HttpStatusCode statusCode, string? scope)
        {
            var body = scope == null ? "{}" : JsonSerializer.Serialize(new { scope });
            _mockHandler.Protected()
                .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
                .ReturnsAsync(new HttpResponseMessage(statusCode) { Content = new StringContent(body) });
        }

        [Test]
        public async Task HasScopeAsync_TokenGrantsRequestedScope_ReturnsTrue()
        {
            SetupTokenInfoResponse(HttpStatusCode.OK, "openid email https://www.googleapis.com/auth/drive.file");

            var result = await _service.HasScopeAsync("https://www.googleapis.com/auth/drive.file", SessionId);

            Assert.That(result, Is.True);
        }

        [Test]
        public async Task HasScopeAsync_TokenDoesNotGrantRequestedScope_ReturnsFalse()
        {
            SetupTokenInfoResponse(HttpStatusCode.OK, "openid email");

            var result = await _service.HasScopeAsync("https://www.googleapis.com/auth/drive.file", SessionId);

            Assert.That(result, Is.False);
        }

        [Test]
        public async Task HasScopeAsync_TokenInfoCallFails_ReturnsFalse()
        {
            SetupTokenInfoResponse(HttpStatusCode.BadRequest, null);

            var result = await _service.HasScopeAsync("openid", SessionId);

            Assert.That(result, Is.False);
        }

        [Test]
        public async Task HasScopeAsync_NoPairedSessionAndNoAuthenticatedUser_ReturnsFalse()
        {
            _mockHttpContextAccessor.Setup(a => a.HttpContext).Returns((HttpContext?)null);

            var result = await _service.HasScopeAsync("openid", "missing-session");

            Assert.That(result, Is.False);
        }
    }
}
