using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BiatecSelfCustodyCore.Providers
{
    /// <summary>
    /// OneDrive-backed <see cref="ICloudStorageProvider"/>: stores the account file in the user's
    /// OneDrive "app folder" (Microsoft Graph's <c>/me/drive/special/approot</c> special folder) -
    /// the OneDrive equivalent of Google Drive's <c>drive.file</c> scope, via plain Graph REST calls
    /// (no Microsoft.Graph SDK dependency). Requires the <c>Files.ReadWrite.AppFolder</c> delegated
    /// Graph permission (see <c>BiatecOIDC/ENTRA_SETUP_GUIDE.md</c>).
    /// </summary>
    public class MicrosoftCloudStorageProvider : ICloudStorageProvider
    {
        /// <summary>Canonical provider name - also the Microsoft Entra OIDC authentication scheme name.</summary>
        public const string ProviderName = "Microsoft";

        private const string GraphBaseUrl = "https://graph.microsoft.com/v1.0";
        private const string AppRootUrl = GraphBaseUrl + "/me/drive/special/approot";

        private readonly HttpClient _httpClient;
        private readonly IHttpContextAccessor _httpContextAccessor;
        private readonly IOptionsMonitor<Model.MicrosoftEntraConfiguration> _entraConfig;
        private readonly ILogger<MicrosoftCloudStorageProvider> _logger;

        public MicrosoftCloudStorageProvider(
            HttpClient httpClient,
            IHttpContextAccessor httpContextAccessor,
            IOptionsMonitor<Model.MicrosoftEntraConfiguration> entraConfig,
            ILogger<MicrosoftCloudStorageProvider> logger)
        {
            _httpClient = httpClient;
            _httpContextAccessor = httpContextAccessor;
            _entraConfig = entraConfig;
            _logger = logger;
        }

        public string Name => ProviderName;
        public string DisplayName => "Microsoft";
        public string RequiredScope => "https://graph.microsoft.com/Files.ReadWrite.AppFolder";

        public bool IsConfigured =>
            !string.IsNullOrWhiteSpace(_entraConfig.CurrentValue.ClientId) &&
            !string.IsNullOrWhiteSpace(_entraConfig.CurrentValue.ClientSecret);

        public async Task<string?> GetAmbientAccessTokenAsync()
        {
            var httpContext = _httpContextAccessor.HttpContext;
            if (httpContext == null)
            {
                return null;
            }

            return await httpContext.GetTokenAsync(ProviderName, "access_token");
        }

        public async Task<string?> GetAmbientRefreshTokenAsync()
        {
            var httpContext = _httpContextAccessor.HttpContext;
            if (httpContext == null)
            {
                return null;
            }

            return await httpContext.GetTokenAsync(ProviderName, "refresh_token");
        }

        public async Task<ProviderTokenRefreshResult?> RefreshAccessTokenAsync(string refreshToken)
        {
            try
            {
                var tokenEndpoint = $"https://login.microsoftonline.com/{_entraConfig.CurrentValue.TenantId}/oauth2/v2.0/token";
                using var request = new HttpRequestMessage(HttpMethod.Post, tokenEndpoint)
                {
                    Content = new FormUrlEncodedContent(new Dictionary<string, string>
                    {
                        ["client_id"] = _entraConfig.CurrentValue.ClientId,
                        ["client_secret"] = _entraConfig.CurrentValue.ClientSecret,
                        ["refresh_token"] = refreshToken,
                        ["grant_type"] = "refresh_token",
                        ["scope"] = $"offline_access {RequiredScope}"
                    })
                };

                using var response = await _httpClient.SendAsync(request);
                if (!response.IsSuccessStatusCode)
                {
                    // Most commonly invalid_grant - the refresh token was revoked or has expired. Treated
                    // as "no renewal available", same as never having had one - the caller falls back to
                    // requiring a fresh interactive sign-in.
                    return null;
                }

                using var payload = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                if (!payload.RootElement.TryGetProperty("access_token", out var accessTokenProperty))
                {
                    return null;
                }

                // Entra ID rotates the refresh token on every use - the one just spent is no longer valid,
                // so the caller must re-cache whatever comes back here (or, if absent, treat renewal as a
                // one-time-only affair for this session).
                var rotatedRefreshToken = payload.RootElement.TryGetProperty("refresh_token", out var refreshTokenProperty)
                    ? refreshTokenProperty.GetString()
                    : null;

                return new ProviderTokenRefreshResult(accessTokenProperty.GetString()!, rotatedRefreshToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to refresh Microsoft access token using the cached refresh token.");
                return null;
            }
        }

        public string BuildAuthorizationUrl(string redirectUri, string state)
        {
            var tenantId = Uri.EscapeDataString(_entraConfig.CurrentValue.TenantId);
            var clientId = Uri.EscapeDataString(_entraConfig.CurrentValue.ClientId);
            var encodedRedirectUri = Uri.EscapeDataString(redirectUri);
            var scope = Uri.EscapeDataString(RequiredScope);
            var encodedState = Uri.EscapeDataString(state);
            return $"https://login.microsoftonline.com/{tenantId}/oauth2/v2.0/authorize?client_id={clientId}&redirect_uri={encodedRedirectUri}&response_type=code&scope={scope}&state={encodedState}";
        }

        public async Task<string?> ExchangeAuthorizationCodeAsync(string code, string redirectUri)
        {
            try
            {
                var tokenEndpoint = $"https://login.microsoftonline.com/{_entraConfig.CurrentValue.TenantId}/oauth2/v2.0/token";
                using var request = new HttpRequestMessage(HttpMethod.Post, tokenEndpoint)
                {
                    Content = new FormUrlEncodedContent(new Dictionary<string, string>
                    {
                        ["client_id"] = _entraConfig.CurrentValue.ClientId,
                        ["client_secret"] = _entraConfig.CurrentValue.ClientSecret,
                        ["code"] = code,
                        ["redirect_uri"] = redirectUri,
                        ["grant_type"] = "authorization_code"
                    })
                };

                using var response = await _httpClient.SendAsync(request);
                if (!response.IsSuccessStatusCode)
                {
                    return null;
                }

                using var payload = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                return payload.RootElement.TryGetProperty("access_token", out var accessTokenProperty)
                    ? accessTokenProperty.GetString()
                    : null;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to exchange a Microsoft authorization code for an access token.");
                return null;
            }
        }

        public async Task<bool> HasWriteAccessAsync(string accessToken)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, AppRootUrl);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                using var response = await _httpClient.SendAsync(request);
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to verify OneDrive app folder access; treating as not granted.");
                return false;
            }
        }

        public async Task<byte[]?> TryDownloadAsync(string fileName, string accessToken)
        {
            using var request = CreateRequest(HttpMethod.Get, fileName, accessToken);
            using var response = await _httpClient.SendAsync(request);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }

            EnsureAuthorized(response);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsByteArrayAsync();
        }

        public async Task UploadAsync(string fileName, byte[] content, string accessToken)
        {
            using var request = CreateRequest(HttpMethod.Put, fileName, accessToken);
            request.Content = new ByteArrayContent(content);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

            using var response = await _httpClient.SendAsync(request);
            EnsureAuthorized(response);
            response.EnsureSuccessStatusCode();
        }

        public async Task DeleteAsync(string fileName, string accessToken)
        {
            try
            {
                var encodedFileName = Uri.EscapeDataString(fileName);
                using var request = new HttpRequestMessage(HttpMethod.Delete, $"{GraphBaseUrl}/me/drive/special/approot:/{encodedFileName}");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                using var response = await _httpClient.SendAsync(request);
                if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.NotFound)
                {
                    _logger.LogWarning("Failed to delete {FileName} from OneDrive after key-generation migration ({StatusCode}); leaving it in place.", fileName, response.StatusCode);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to delete {FileName} from OneDrive after key-generation migration; leaving it in place.", fileName);
            }
        }

        private static HttpRequestMessage CreateRequest(HttpMethod method, string fileName, string accessToken)
        {
            var encodedFileName = Uri.EscapeDataString(fileName);
            var url = $"{GraphBaseUrl}/me/drive/special/approot:/{encodedFileName}:/content";
            var request = new HttpRequestMessage(method, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            return request;
        }

        private static void EnsureAuthorized(HttpResponseMessage response)
        {
            if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
            {
                throw new UnauthorizedAccessException("OneDrive access denied. The access token may be expired, invalid, or missing the Files.ReadWrite.AppFolder permission.");
            }
        }
    }
}
