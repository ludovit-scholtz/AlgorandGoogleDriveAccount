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
        private Mock<IGoogleAuthProvider> _mockGoogleAuth = null!;
        private GoogleCloudStorageProvider _provider = null!;

        [SetUp]
        public void SetUp()
        {
            _mockHandler = new Mock<HttpMessageHandler>();
            var httpClient = new HttpClient(_mockHandler.Object);
            _mockGoogleAuth = new Mock<IGoogleAuthProvider>();
            var mockConfig = new Mock<IOptionsMonitor<Configuration>>();
            mockConfig.Setup(c => c.CurrentValue).Returns(new Configuration());
            var mockGoogleServiceConfig = new Mock<IOptionsMonitor<GoogleCloudServiceConfiguration>>();
            mockGoogleServiceConfig.Setup(c => c.CurrentValue).Returns(new GoogleCloudServiceConfiguration { ClientId = "client-id", ClientSecret = "client-secret" });

            _provider = new GoogleCloudStorageProvider(
                _mockGoogleAuth.Object,
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

        [Test]
        public async Task GetAmbientAccessTokenAsync_NoAuthenticatedCookieSession_ReturnsNullInsteadOfThrowing()
        {
            // GoogleAuthProvider.GetCredentialAsync throws InvalidOperationException when the current
            // request has no authenticated cookie session - always true for bearer-token API callers
            // (e.g. WalletController), which have no ambient cookie session at all. The interface contract
            // is to return null here (like MicrosoftCloudStorageProvider's equivalent), so callers can
            // treat it the same as "caller didn't supply a token" rather than getting an unhandled 500.
            _mockGoogleAuth
                .Setup(g => g.GetCredentialAsync(It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("Cannot get credential when not authenticated."));

            var result = await _provider.GetAmbientAccessTokenAsync();

            Assert.That(result, Is.Null);
        }
    }
}
