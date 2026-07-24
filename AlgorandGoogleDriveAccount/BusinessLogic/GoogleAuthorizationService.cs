using AlgorandGoogleDriveAccount.Model;
using Google.Apis.Auth.AspNetCore3;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;
using System.Text.Json;

namespace AlgorandGoogleDriveAccount.BusinessLogic
{
    public class GoogleAuthorizationService : IGoogleAuthorizationService
    {
        private readonly IDistributedCache _cache;
        private readonly ILogger<GoogleAuthorizationService> _logger;
        private readonly IHttpContextAccessor _httpContextAccessor;
        private readonly IOptionsMonitor<Configuration> _config;
        private readonly IHttpClientFactory _httpClientFactory;

        public GoogleAuthorizationService(
            IDistributedCache cache,
            ILogger<GoogleAuthorizationService> logger,
            IHttpContextAccessor httpContextAccessor,
            IOptionsMonitor<Configuration> config,
            IHttpClientFactory httpClientFactory)
        {
            _cache = cache;
            _logger = logger;
            _httpContextAccessor = httpContextAccessor;
            _config = config;
            _httpClientFactory = httpClientFactory;
        }

        public async Task<string> GetIncrementalAuthorizationUrlAsync(string[] additionalScopes, string? sessionId = null, string? redirectUri = null)
        {
            if (additionalScopes == null || additionalScopes.Length == 0)
            {
                throw new ArgumentException("Additional scopes are required for incremental authorization", nameof(additionalScopes));
            }

            var scopeString = string.Join(" ", additionalScopes);
            
            // For device pairing scenarios, we need to construct the URL manually
            var clientId = _config.CurrentValue.ClientId;
            var baseUrl = "https://accounts.google.com/o/oauth2/v2/auth";
            var parameters = new Dictionary<string, string>
            {
                ["client_id"] = clientId,
                ["response_type"] = "code",
                ["scope"] = scopeString,
                ["include_granted_scopes"] = "true",
                ["access_type"] = "offline",
                ["redirect_uri"] = redirectUri ?? $"{_config.CurrentValue.Host}/api/device/drive-access-callback"
            };

            if (!string.IsNullOrEmpty(sessionId))
            {
                parameters["state"] = sessionId;
            }

            var queryString = string.Join("&", parameters.Select(kvp => $"{kvp.Key}={Uri.EscapeDataString(kvp.Value)}"));
            return $"{baseUrl}?{queryString}";
        }

        public async Task<bool> HasScopeAsync(string scope, string? sessionId = null)
        {
            try
            {
                string? accessToken = null;

                if (!string.IsNullOrEmpty(sessionId))
                {
                    var cacheKey = $"device_session:{sessionId}";
                    var deviceInfoJson = await _cache.GetStringAsync(cacheKey);

                    if (!string.IsNullOrEmpty(deviceInfoJson))
                    {
                        var deviceInfo = JsonSerializer.Deserialize<PairedDeviceInfo>(deviceInfoJson);
                        accessToken = deviceInfo?.AccessToken;
                    }
                }
                else
                {
                    // For regular authentication, check the current user's token
                    var httpContext = _httpContextAccessor.HttpContext;
                    if (httpContext?.User?.Identity?.IsAuthenticated == true)
                    {
                        accessToken = await httpContext.GetTokenAsync("access_token");
                    }
                }

                if (string.IsNullOrEmpty(accessToken))
                {
                    return false;
                }

                return await TokenGrantsScopeAsync(accessToken, scope);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error checking scope {scope} for session {sessionId}");
                return false;
            }
        }

        /// <summary>
        /// Calls Google's tokeninfo endpoint to determine the scopes actually granted to
        /// <paramref name="accessToken"/> and checks whether <paramref name="scope"/> is among them.
        /// Fails closed (returns <c>false</c>) if the token is invalid or the check itself fails.
        /// </summary>
        private async Task<bool> TokenGrantsScopeAsync(string accessToken, string scope)
        {
            var tokenInfoUrl = $"https://oauth2.googleapis.com/tokeninfo?access_token={Uri.EscapeDataString(accessToken)}";
            var httpClient = _httpClientFactory.CreateClient();
            var response = await httpClient.GetAsync(tokenInfoUrl);
            if (!response.IsSuccessStatusCode)
            {
                return false;
            }

            using var tokenInfo = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            if (!tokenInfo.RootElement.TryGetProperty("scope", out var scopeProperty))
            {
                return false;
            }

            var grantedScopes = (scopeProperty.GetString() ?? string.Empty)
                .Split(' ', StringSplitOptions.RemoveEmptyEntries);

            return grantedScopes.Contains(scope, StringComparer.Ordinal);
        }

        public async Task<string?> GetAccessTokenWithScopeAsync(string scope, string? sessionId = null)
        {
            try
            {
                if (!string.IsNullOrEmpty(sessionId))
                {
                    var cacheKey = $"device_session:{sessionId}";
                    var deviceInfoJson = await _cache.GetStringAsync(cacheKey);
                    
                    if (!string.IsNullOrEmpty(deviceInfoJson))
                    {
                        var deviceInfo = JsonSerializer.Deserialize<PairedDeviceInfo>(deviceInfoJson);
                        return deviceInfo?.AccessToken;
                    }
                }

                var httpContext = _httpContextAccessor.HttpContext;
                if (httpContext?.User?.Identity?.IsAuthenticated == true)
                {
                    return await httpContext.GetTokenAsync("access_token");
                }

                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error getting access token with scope {scope} for session {sessionId}");
                return null;
            }
        }
    }
}