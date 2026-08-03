using System.Security.Claims;
using BiatecSelfCustodyCore.Providers;
using Microsoft.AspNetCore.Http;
using Moq;

namespace BiatecMCPTests
{
    /// <summary>
    /// Covers <see cref="MockCloudStorageProvider"/>/<see cref="MockCloudStorage"/> - the test/dev-only
    /// in-memory cloud storage provider backing BiatecOIDC's mock testing feature (see
    /// <c>BiatecOIDC/MOCK_TESTING.md</c>). In particular, regression coverage for the bug where
    /// <c>JwtIssuerController.AuthorizeConsent</c>/<c>AuthorizeCallback</c> called
    /// <c>HttpContext.GetTokenAsync(provider.Name, ...)</c>, which throws
    /// <see cref="InvalidOperationException"/> for a provider with no registered ASP.NET Core authentication
    /// scheme (true for Mock, which signs straight into the cookie scheme) - fixed by switching those call
    /// sites to <see cref="ICloudStorageProvider.GetAmbientAccessTokenAsync"/>, which this provider
    /// implements without touching the authentication-scheme system at all.
    /// </summary>
    [TestFixture]
    public class MockCloudStorageProviderTests
    {
        private const string TestEmail = "mock-user@example.com";

        private MockCloudStorage _storage = null!;
        private DefaultHttpContext _httpContext = null!;
        private MockCloudStorageProvider _provider = null!;

        [SetUp]
        public void SetUp()
        {
            _storage = new MockCloudStorage();
            _httpContext = new DefaultHttpContext();
            var httpContextAccessor = Mock.Of<IHttpContextAccessor>(a => a.HttpContext == _httpContext);
            _provider = new MockCloudStorageProvider(_storage, httpContextAccessor);
        }

        private void SignIn(string email) =>
            _httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.Email, email) }, "test"));

        [Test]
        public void Name_IsMock() => Assert.That(_provider.Name, Is.EqualTo("Mock"));

        [Test]
        public void IsConfigured_AlwaysTrue() => Assert.That(_provider.IsConfigured, Is.True);

        [Test]
        public async Task GetAmbientAccessTokenAsync_SignedIn_ReturnsDeterministicMockToken()
        {
            SignIn(TestEmail);

            var token = await _provider.GetAmbientAccessTokenAsync();

            Assert.That(token, Is.EqualTo("mock:" + TestEmail));
        }

        [Test]
        public async Task GetAmbientAccessTokenAsync_NotSignedIn_ReturnsNull()
        {
            // _httpContext.User defaults to an unauthenticated/empty ClaimsPrincipal - no email claim.
            var token = await _provider.GetAmbientAccessTokenAsync();

            Assert.That(token, Is.Null);
        }

        [Test]
        public async Task GetAmbientRefreshTokenAsync_AlwaysReturnsNull()
        {
            SignIn(TestEmail);

            Assert.That(await _provider.GetAmbientRefreshTokenAsync(), Is.Null);
        }

        [Test]
        public async Task RefreshAccessTokenAsync_AlwaysReturnsNull()
        {
            Assert.That(await _provider.RefreshAccessTokenAsync("anything"), Is.Null);
        }

        [Test]
        public async Task HasWriteAccessAsync_ValidMockToken_ReturnsTrue()
        {
            var token = MockCloudStorageProvider.BuildMockAccessToken(TestEmail);

            Assert.That(await _provider.HasWriteAccessAsync(token), Is.True);
        }

        [Test]
        public async Task HasWriteAccessAsync_NotAMockToken_ReturnsFalse()
        {
            Assert.That(await _provider.HasWriteAccessAsync("some-other-token"), Is.False);
        }

        [Test]
        public async Task HasWriteAccessAsync_EmptyToken_ReturnsFalse()
        {
            Assert.That(await _provider.HasWriteAccessAsync(string.Empty), Is.False);
        }

        [Test]
        public async Task UploadAsync_ThenTryDownloadAsync_RoundTripsForTheSameEmail()
        {
            var token = MockCloudStorageProvider.BuildMockAccessToken(TestEmail);
            var content = new byte[] { 1, 2, 3, 4 };

            await _provider.UploadAsync("file.dat", content, token);
            var downloaded = await _provider.TryDownloadAsync("file.dat", token);

            Assert.That(downloaded, Is.EqualTo(content));
        }

        [Test]
        public async Task TryDownloadAsync_FileNeverUploaded_ReturnsNull()
        {
            var token = MockCloudStorageProvider.BuildMockAccessToken(TestEmail);

            Assert.That(await _provider.TryDownloadAsync("never-uploaded.dat", token), Is.Null);
        }

        [Test]
        public async Task UploadAsync_DifferentEmails_AreIsolatedFromEachOther()
        {
            var tokenA = MockCloudStorageProvider.BuildMockAccessToken("a@example.com");
            var tokenB = MockCloudStorageProvider.BuildMockAccessToken("b@example.com");

            await _provider.UploadAsync("file.dat", new byte[] { 1 }, tokenA);

            Assert.That(await _provider.TryDownloadAsync("file.dat", tokenB), Is.Null);
        }

        [Test]
        public void TryDownloadAsync_InvalidToken_ThrowsUnauthorizedAccessException()
        {
            Assert.That(async () => await _provider.TryDownloadAsync("file.dat", "not-a-mock-token"),
                Throws.TypeOf<UnauthorizedAccessException>());
        }

        [Test]
        public void UploadAsync_InvalidToken_ThrowsUnauthorizedAccessException()
        {
            Assert.That(async () => await _provider.UploadAsync("file.dat", new byte[] { 1 }, "not-a-mock-token"),
                Throws.TypeOf<UnauthorizedAccessException>());
        }

        [Test]
        public void DeleteAsync_InvalidToken_DoesNotThrow()
        {
            // Best-effort per the ICloudStorageProvider contract - DeleteAsync must never throw.
            Assert.DoesNotThrowAsync(async () => await _provider.DeleteAsync("file.dat", "not-a-mock-token"));
        }

        [Test]
        public async Task DeleteAsync_RemovesUploadedFile()
        {
            var token = MockCloudStorageProvider.BuildMockAccessToken(TestEmail);
            await _provider.UploadAsync("file.dat", new byte[] { 1 }, token);

            await _provider.DeleteAsync("file.dat", token);

            Assert.That(await _provider.TryDownloadAsync("file.dat", token), Is.Null);
        }

        [Test]
        public void BuildAuthorizationUrl_ThrowsNotSupported()
        {
            Assert.Throws<NotSupportedException>(() => _provider.BuildAuthorizationUrl("https://example.invalid/cb", "state"));
        }

        [Test]
        public void ExchangeAuthorizationCodeAsync_ThrowsNotSupported()
        {
            Assert.That(async () => await _provider.ExchangeAuthorizationCodeAsync("code", "https://example.invalid/cb"),
                Throws.TypeOf<NotSupportedException>());
        }
    }
}
