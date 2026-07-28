using BiatecSelfCustodyCore.Model;
using Microsoft.Extensions.Logging;
using System.Net.Http.Headers;
using System.Text.Json;

namespace BiatecSelfCustodyCore.BusinessLogic
{
    /// <summary>
    /// Checks whether an access token actually grants storage-write access (Google's <c>drive.file</c>
    /// scope, or Microsoft Graph's <c>Files.ReadWrite.AppFolder</c>) - used right before finalizing an
    /// OIDC authorization or a device pairing, so a session is never completed with a token that can't
    /// actually read/write the self-custody account file.
    /// </summary>
    public class StorageAccessVerifier
    {
        private const string GoogleDriveFileScope = "https://www.googleapis.com/auth/drive.file";

        private readonly HttpClient _httpClient;
        private readonly ILogger<StorageAccessVerifier> _logger;

        public StorageAccessVerifier(HttpClient httpClient, ILogger<StorageAccessVerifier> logger)
        {
            _httpClient = httpClient;
            _logger = logger;
        }

        public Task<bool> HasWriteAccessAsync(string accessToken, StorageProvider provider) => provider switch
        {
            StorageProvider.Microsoft => HasOneDriveAppFolderAccessAsync(accessToken),
            _ => HasGoogleDriveFileAccessAsync(accessToken)
        };

        private async Task<bool> HasGoogleDriveFileAccessAsync(string accessToken)
        {
            try
            {
                var tokenInfoUrl = $"https://oauth2.googleapis.com/tokeninfo?access_token={Uri.EscapeDataString(accessToken)}";
                var response = await _httpClient.GetAsync(tokenInfoUrl);
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

                return grantedScopes.Contains(GoogleDriveFileScope, StringComparer.Ordinal);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to verify Google Drive write access; treating as not granted.");
                return false;
            }
        }

        private async Task<bool> HasOneDriveAppFolderAccessAsync(string accessToken)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, "https://graph.microsoft.com/v1.0/me/drive/special/approot");
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
    }
}
