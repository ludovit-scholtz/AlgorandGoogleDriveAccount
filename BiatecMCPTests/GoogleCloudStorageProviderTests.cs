using System.Net;
using System.Text.Json;
using BiatecSelfCustodyCore.Model;
using BiatecSelfCustodyCore.Providers;
using Google.Apis.Auth.AspNetCore3;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Moq.Protected;

namespace BiatecMCPTests
{
    /// <summary>
    /// Covers <see cref="GoogleCloudStorageProvider.HasWriteAccessAsync"/> - the tokeninfo-based
    /// <c>drive.file</c> scope check used before finalizing a sign-in/pairing. Drive
    /// download/upload behavior is covered indirectly via <c>DriveControllerTests</c> and manual
    /// verification (it talks to the real Google Drive API client, not easily unit-testable
    /// without a live account).
    /// </summary>
    [TestFixture]
    public class GoogleCloudStorageProviderTests
    {
        private Mock<HttpMessageHandler> _mockHandler = null!;
        private GoogleCloudStorageProvider _provider = null!;

        [SetUp]
        public void SetUp()
        {
            _mockHandler = new Mock<HttpMessageHandler>();
            var httpClient = new HttpClient(_mockHandler.Object);
            var mockGoogleAuth = new Mock<IGoogleAuthProvider>();
            var mockConfig = new Mock<IOptionsMonitor<Configuration>>();
            mockConfig.Setup(c => c.CurrentValue).Returns(new Configuration());
            var mockGoogleServiceConfig = new Mock<IOptionsMonitor<GoogleCloudServiceConfiguration>>();
            mockGoogleServiceConfig.Setup(c => c.CurrentValue).Returns(new GoogleCloudServiceConfiguration { ClientId = "client-id", ClientSecret = "client-secret" });

            _provider = new GoogleCloudStorageProvider(
                mockGoogleAuth.Object,
                mockConfig.Object,
                mockGoogleServiceConfig.Object,
                httpClient,
                new Mock<ILogger<GoogleCloudStorageProvider>>().Object);
        }

        private void SetupResponse(HttpStatusCode statusCode, string? body = null)
        {
            _mockHandler.Protected()
                .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
                .ReturnsAsync(new HttpResponseMessage(statusCode) { Content = new StringContent(body ?? "{}") });
        }

        [Test]
        public async Task HasWriteAccessAsync_TokenGrantsDriveFileScope_ReturnsTrue()
        {
            SetupResponse(HttpStatusCode.OK, JsonSerializer.Serialize(new { scope = "openid email https://www.googleapis.com/auth/drive.file" }));

            var result = await _provider.HasWriteAccessAsync("token");

            Assert.That(result, Is.True);
        }

        [Test]
        public async Task HasWriteAccessAsync_TokenMissingDriveFileScope_ReturnsFalse()
        {
            SetupResponse(HttpStatusCode.OK, JsonSerializer.Serialize(new { scope = "openid email" }));

            var result = await _provider.HasWriteAccessAsync("token");

            Assert.That(result, Is.False);
        }

        [Test]
        public async Task HasWriteAccessAsync_TokenInfoCallFails_ReturnsFalse()
        {
            SetupResponse(HttpStatusCode.BadRequest);

            var result = await _provider.HasWriteAccessAsync("token");

            Assert.That(result, Is.False);
        }

        [Test]
        public void Name_IsGoogle()
        {
            Assert.That(_provider.Name, Is.EqualTo("Google"));
        }
    }
}
