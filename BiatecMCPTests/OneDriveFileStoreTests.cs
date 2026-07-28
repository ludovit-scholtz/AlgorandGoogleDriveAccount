using BiatecSelfCustodyCore.Repository;
using Moq;
using Moq.Protected;
using System.Net;
using System.Net.Http.Headers;

namespace BiatecMCPTests
{
    [TestFixture]
    public class OneDriveFileStoreTests
    {
        private Mock<HttpMessageHandler> _mockHandler = null!;
        private OneDriveFileStore _store = null!;
        private HttpRequestMessage? _capturedRequest;
        private byte[]? _capturedRequestBody;

        [SetUp]
        public void SetUp()
        {
            _mockHandler = new Mock<HttpMessageHandler>();
            var httpClient = new HttpClient(_mockHandler.Object);
            _store = new OneDriveFileStore(httpClient);
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

            var result = await _store.TryDownloadAsync("AVMAccount.dat", "token");

            Assert.That(result, Is.EqualTo(expected));
            Assert.That(_capturedRequest!.Method, Is.EqualTo(HttpMethod.Get));
            Assert.That(_capturedRequest.RequestUri!.ToString(), Does.Contain("/me/drive/special/approot:/AVMAccount.dat:/content"));
            Assert.That(_capturedRequest.Headers.Authorization, Is.EqualTo(new AuthenticationHeaderValue("Bearer", "token")));
        }

        [Test]
        public async Task TryDownloadAsync_FileDoesNotExist_ReturnsNull()
        {
            SetupResponse(HttpStatusCode.NotFound);

            var result = await _store.TryDownloadAsync("AVMAccount.dat", "token");

            Assert.That(result, Is.Null);
        }

        [Test]
        public void TryDownloadAsync_Unauthorized_ThrowsUnauthorizedAccessException()
        {
            SetupResponse(HttpStatusCode.Unauthorized);

            Assert.That(async () => await _store.TryDownloadAsync("AVMAccount.dat", "token"),
                Throws.InstanceOf<UnauthorizedAccessException>());
        }

        [Test]
        public void TryDownloadAsync_Forbidden_ThrowsUnauthorizedAccessException()
        {
            // Missing/insufficient Files.ReadWrite.AppFolder consent surfaces as 403, not 401.
            SetupResponse(HttpStatusCode.Forbidden);

            Assert.That(async () => await _store.TryDownloadAsync("AVMAccount.dat", "token"),
                Throws.InstanceOf<UnauthorizedAccessException>());
        }

        [Test]
        public async Task UploadAsync_Success_SendsPutWithContent()
        {
            SetupResponse(HttpStatusCode.OK);
            var content = new byte[] { 5, 6, 7 };

            await _store.UploadAsync("AVMAccount.dat", content, "token");

            Assert.That(_capturedRequest!.Method, Is.EqualTo(HttpMethod.Put));
            Assert.That(_capturedRequestBody, Is.EqualTo(content));
        }

        [Test]
        public void UploadAsync_Unauthorized_ThrowsUnauthorizedAccessException()
        {
            SetupResponse(HttpStatusCode.Unauthorized);

            Assert.That(async () => await _store.UploadAsync("AVMAccount.dat", new byte[] { 1 }, "token"),
                Throws.InstanceOf<UnauthorizedAccessException>());
        }
    }
}
