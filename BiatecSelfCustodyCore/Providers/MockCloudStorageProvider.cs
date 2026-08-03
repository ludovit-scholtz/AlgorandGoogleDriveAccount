using System.Security.Claims;
using Microsoft.AspNetCore.Http;

namespace BiatecSelfCustodyCore.Providers
{
    /// <summary>
    /// In-memory <see cref="ICloudStorageProvider"/> for testing the full OIDC authorize/token flow (and
    /// downstream Algorand/Ethereum signing) end to end, without a real Google/Microsoft account - test/dev
    /// tooling only, see <c>BiatecOIDC/MOCK_TESTING.md</c>. Deliberately never registered unless
    /// <c>CloudServices:Mock:Enabled</c> is explicitly set and at least one account is configured (see
    /// <c>BiatecOIDC/Program.cs</c>), so it can never appear as a sign-in option in a deployment that
    /// hasn't opted into it.
    ///
    /// There is no real OAuth token here - <see cref="GetAmbientAccessTokenAsync"/> instead synthesizes a
    /// deterministic pseudo-token (<c>"mock:{email}"</c>) from the ambient cookie session's own email
    /// claim, and <see cref="TryDownloadAsync"/>/<see cref="UploadAsync"/>/<see cref="DeleteAsync"/> parse
    /// that same pseudo-token back out to know which mock account's in-memory files to touch - this is
    /// what lets every other layer (<c>ICloudAccountRepository</c>, <c>WalletController</c>, ...) treat a
    /// mock sign-in exactly like a real one, with zero special-casing beyond this class.
    /// </summary>
    public sealed class MockCloudStorageProvider : ICloudStorageProvider
    {
        /// <summary>Canonical provider name.</summary>
        public const string ProviderName = "Mock";

        private const string TokenPrefix = "mock:";

        private readonly MockCloudStorage _storage;
        private readonly IHttpContextAccessor _httpContextAccessor;

        public MockCloudStorageProvider(MockCloudStorage storage, IHttpContextAccessor httpContextAccessor)
        {
            _storage = storage;
            _httpContextAccessor = httpContextAccessor;
        }

        public string Name => ProviderName;
        public string DisplayName => "Mock (Testing)";
        public string RequiredScope => string.Empty;

        /// <summary>
        /// Always <c>true</c> - the actual gate is whether this provider is registered in DI at all (see
        /// <c>Program.cs</c>), which only happens when <c>CloudServices:Mock:Enabled</c> is explicitly set
        /// and at least one account is configured. Once registered, it's meant to show up as a sign-in
        /// option (see <c>JwtIssuerController.SelectProvider</c>), so there's nothing further to gate here.
        /// </summary>
        public bool IsConfigured => true;

        public static string BuildMockAccessToken(string email) => TokenPrefix + email;

        private static string ParseEmail(string accessToken)
        {
            if (string.IsNullOrEmpty(accessToken) || !accessToken.StartsWith(TokenPrefix, StringComparison.Ordinal))
            {
                throw new UnauthorizedAccessException("Invalid mock access token.");
            }

            return accessToken[TokenPrefix.Length..];
        }

        public Task<byte[]?> TryDownloadAsync(string fileName, string accessToken) =>
            Task.FromResult(_storage.TryGet(ParseEmail(accessToken), fileName));

        public Task UploadAsync(string fileName, byte[] content, string accessToken)
        {
            _storage.Set(ParseEmail(accessToken), fileName, content);
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string fileName, string accessToken)
        {
            try
            {
                _storage.Delete(ParseEmail(accessToken), fileName);
            }
            catch (UnauthorizedAccessException)
            {
                // Best-effort per the interface contract - never throws.
            }

            return Task.CompletedTask;
        }

        public Task<bool> HasWriteAccessAsync(string accessToken) =>
            Task.FromResult(!string.IsNullOrEmpty(accessToken) && accessToken.StartsWith(TokenPrefix, StringComparison.Ordinal));

        public Task<string?> GetAmbientAccessTokenAsync()
        {
            var email = _httpContextAccessor.HttpContext?.User?.FindFirst(ClaimTypes.Email)?.Value;
            return Task.FromResult(string.IsNullOrWhiteSpace(email) ? null : BuildMockAccessToken(email));
        }

        /// <summary>
        /// Always <c>null</c> - the mock access token never expires and needs no renewal (it's just a
        /// deterministic function of the signed-in email), so there's nothing to refresh.
        /// </summary>
        public Task<string?> GetAmbientRefreshTokenAsync() => Task.FromResult<string?>(null);

        /// <summary>Never called in practice (<see cref="GetAmbientRefreshTokenAsync"/> never returns one to renew) - returns "no renewal available" if it ever is.</summary>
        public Task<ProviderTokenRefreshResult?> RefreshAccessTokenAsync(string refreshToken) => Task.FromResult<ProviderTokenRefreshResult?>(null);

        public string BuildAuthorizationUrl(string redirectUri, string state) =>
            throw new NotSupportedException("Cross-cloud vault backup is not supported for the Mock testing provider.");

        public Task<string?> ExchangeAuthorizationCodeAsync(string code, string redirectUri) =>
            throw new NotSupportedException("Cross-cloud vault backup is not supported for the Mock testing provider.");
    }
}
