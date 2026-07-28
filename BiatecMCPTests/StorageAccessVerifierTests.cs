using BiatecSelfCustodyCore.BusinessLogic;
using BiatecSelfCustodyCore.Model;
using Microsoft.Extensions.Logging;
using Moq;
using Moq.Protected;
using System.Net;
using System.Text.Json;

namespace BiatecMCPTests
{
    [TestFixture]
    public class StorageAccessVerifierTests
    {
        private Mock<HttpMessageHandler> _mockHandler = null!;
        private StorageAccessVerifier _verifier = null!;

        [SetUp]
        public void SetUp()
        {
            _mockHandler = new Mock<HttpMessageHandler>();
            var httpClient = new HttpClient(_mockHandler.Object);
            _verifier = new StorageAccessVerifier(httpClient, new Mock<ILogger<StorageAccessVerifier>>().Object);
        }

        private void SetupResponse(HttpStatusCode statusCode, string? body = null)
        {
            _mockHandler.Protected()
                .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
                .ReturnsAsync(new HttpResponseMessage(statusCode) { Content = new StringContent(body ?? "{}") });
        }

        [Test]
        public async Task HasWriteAccessAsync_Google_TokenGrantsDriveFileScope_ReturnsTrue()
        {
            SetupResponse(HttpStatusCode.OK, JsonSerializer.Serialize(new { scope = "openid email https://www.googleapis.com/auth/drive.file" }));

            var result = await _verifier.HasWriteAccessAsync("token", StorageProvider.Google);

            Assert.That(result, Is.True);
        }

        [Test]
        public async Task HasWriteAccessAsync_Google_TokenMissingDriveFileScope_ReturnsFalse()
        {
            SetupResponse(HttpStatusCode.OK, JsonSerializer.Serialize(new { scope = "openid email" }));

            var result = await _verifier.HasWriteAccessAsync("token", StorageProvider.Google);

            Assert.That(result, Is.False);
        }

        [Test]
        public async Task HasWriteAccessAsync_Google_TokenInfoCallFails_ReturnsFalse()
        {
            SetupResponse(HttpStatusCode.BadRequest);

            var result = await _verifier.HasWriteAccessAsync("token", StorageProvider.Google);

            Assert.That(result, Is.False);
        }

        [Test]
        public async Task HasWriteAccessAsync_Microsoft_AppFolderReachable_ReturnsTrue()
        {
            SetupResponse(HttpStatusCode.OK, JsonSerializer.Serialize(new { id = "approot-id" }));

            var result = await _verifier.HasWriteAccessAsync("token", StorageProvider.Microsoft);

            Assert.That(result, Is.True);
        }

        [Test]
        public async Task HasWriteAccessAsync_Microsoft_AppFolderForbidden_ReturnsFalse()
        {
            SetupResponse(HttpStatusCode.Forbidden);

            var result = await _verifier.HasWriteAccessAsync("token", StorageProvider.Microsoft);

            Assert.That(result, Is.False);
        }
    }
}
