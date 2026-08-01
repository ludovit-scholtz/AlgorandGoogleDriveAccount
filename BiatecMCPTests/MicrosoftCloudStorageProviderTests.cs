using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using BiatecSelfCustodyCore.Model;
using BiatecSelfCustodyCore.Providers;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Moq.Protected;

namespace BiatecMCPTests
{
    [TestFixture]
    public class MicrosoftCloudStorageProviderTests
    {
        private Mock<HttpMessageHandler> _mockHandler = null!;
        private MicrosoftCloudStorageProvider _provider = null!;
        private HttpRequestMessage? _capturedRequest;
        private byte[]? _capturedRequestBody;

        [SetUp]
        public void SetUp()
        {
            _mockHandler = new Mock<HttpMessageHandler>();
            var httpClient = new HttpClient(_mockHandler.Object);
            var httpContextAccessor = new Mock<IHttpContextAccessor>();
            httpContextAccessor.Setup(a => a.HttpContext).Returns((HttpContext?)null);
            var mockEntraConfig = new Mock<IOptionsMonitor<MicrosoftEntraConfiguration>>();
            mockEntraConfig.Setup(c => c.CurrentValue).Returns(new MicrosoftEntraConfiguration { ClientId = "client-id", ClientSecret = "client-secret" });

            _provider = new MicrosoftCloudStorageProvider(
                httpClient,
                httpContextAccessor.Object,
                mockEntraConfig.Object,
                new Mock<ILogger<MicrosoftCloudStorageProvider>>().Object);
        }

        private void SetupResponse(HttpStatusCode statusCode, byte[]? content = null)
        {
            _mockHandler.Protected()
                .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
                .Callback<HttpRequestMessage, CancellationToken>((request, _) =>
                {
                    _capturedRequest = request;
                    // Read the body now, synchronously - by the time UploadAsync's `using request` block
                    // disposes it after SendAsync returns, ReadAsByteArrayAsync would throw ObjectDisposedException.
                    _capturedRequestBody = request.Content?.ReadAsByteArrayAsync().GetAwaiter().GetResult();
                })
                .ReturnsAsync(new HttpResponseMessage(statusCode)
                {
                    Content = content != null ? new ByteArrayContent(content) : null
                });
        }

        [Test]
        public async Task TryDownloadAsync_FileExists_ReturnsBytes()
        {
            var expected = new byte[] { 1, 2, 3, 4 };
            SetupResponse(HttpStatusCode.OK, expected);

            var result = await _provider.TryDownloadAsync("AVMAccount.dat", "token");

            Assert.That(result, Is.EqualTo(expected));
            Assert.That(_capturedRequest!.Method, Is.EqualTo(HttpMethod.Get));
            Assert.That(_capturedRequest.RequestUri!.ToString(), Does.Contain("/me/drive/special/approot:/AVMAccount.dat:/content"));
            Assert.That(_capturedRequest.Headers.Authorization, Is.EqualTo(new AuthenticationHeaderValue("Bearer", "token")));
        }

        [Test]
        public async Task TryDownloadAsync_FileDoesNotExist_ReturnsNull()
        {
            SetupResponse(HttpStatusCode.NotFound);

            var result = await _provider.TryDownloadAsync("AVMAccount.dat", "token");

            Assert.That(result, Is.Null);
        }

        [Test]
        public void TryDownloadAsync_Unauthorized_ThrowsUnauthorizedAccessException()
        {
            SetupResponse(HttpStatusCode.Unauthorized);

            Assert.That(async () => await _provider.TryDownloadAsync("AVMAccount.dat", "token"),
                Throws.InstanceOf<UnauthorizedAccessException>());
        }

        [Test]
        public void TryDownloadAsync_Forbidden_ThrowsUnauthorizedAccessException()
        {
            // Missing/insufficient Files.ReadWrite.AppFolder consent surfaces as 403, not 401.
            SetupResponse(HttpStatusCode.Forbidden);

            Assert.That(async () => await _provider.TryDownloadAsync("AVMAccount.dat", "token"),
                Throws.InstanceOf<UnauthorizedAccessException>());
        }

        [Test]
        public async Task UploadAsync_Success_SendsPutWithContent()
        {
            SetupResponse(HttpStatusCode.OK);
            var content = new byte[] { 5, 6, 7 };

            await _provider.UploadAsync("AVMAccount.dat", content, "token");

            Assert.That(_capturedRequest!.Method, Is.EqualTo(HttpMethod.Put));
            Assert.That(_capturedRequestBody, Is.EqualTo(content));
        }

        [Test]
        public void UploadAsync_Unauthorized_ThrowsUnauthorizedAccessException()
        {
            SetupResponse(HttpStatusCode.Unauthorized);

            Assert.That(async () => await _provider.UploadAsync("AVMAccount.dat", new byte[] { 1 }, "token"),
                Throws.InstanceOf<UnauthorizedAccessException>());
        }

        [Test]
        public async Task HasWriteAccessAsync_AppFolderReachable_ReturnsTrue()
        {
            SetupResponse(HttpStatusCode.OK, System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { id = "approot-id" })));

            var result = await _provider.HasWriteAccessAsync("token");

            Assert.That(result, Is.True);
        }

        [Test]
        public async Task HasWriteAccessAsync_AppFolderForbidden_ReturnsFalse()
        {
            SetupResponse(HttpStatusCode.Forbidden);

            var result = await _provider.HasWriteAccessAsync("token");

            Assert.That(result, Is.False);
        }

        [Test]
        public void Name_IsMicrosoft()
        {
            Assert.That(_provider.Name, Is.EqualTo("Microsoft"));
        }

        [Test]
        public async Task RefreshAccessTokenAsync_Success_ReturnsNewAccessTokenAndRotatedRefreshToken()
        {
            // Entra ID always rotates the refresh token on use - the caller must re-cache the new one.
            SetupResponse(HttpStatusCode.OK, System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
            {
                access_token = "new-access-token",
                refresh_token = "rotated-refresh-token",
                expires_in = 3600
            })));

            var result = await _provider.RefreshAccessTokenAsync("some-refresh-token");

            Assert.That(result, Is.Not.Null);
            Assert.That(result!.AccessToken, Is.EqualTo("new-access-token"));
            Assert.That(result.RefreshToken, Is.EqualTo("rotated-refresh-token"));
        }

        [Test]
        public async Task RefreshAccessTokenAsync_RevokedRefreshToken_ReturnsNull()
        {
            SetupResponse(HttpStatusCode.BadRequest, System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { error = "invalid_grant" })));

            var result = await _provider.RefreshAccessTokenAsync("revoked-refresh-token");

            Assert.That(result, Is.Null);
        }
    }
}
