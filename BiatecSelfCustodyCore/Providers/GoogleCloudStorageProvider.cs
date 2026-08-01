using System.Text.Json;
using Google.Apis.Auth.AspNetCore3;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Drive.v3;
using Google.Apis.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BiatecSelfCustodyCore.Providers
{
    /// <summary>
    /// Google Drive-backed <see cref="ICloudStorageProvider"/>: stores the account file in a Drive
    /// folder (created on first use, named per <see cref="Model.Configuration.StorageFolderName"/>)
    /// owned by the app, scoped to the <c>drive.file</c> permission (the app can only ever see files
    /// it created itself).
    /// </summary>
    public class GoogleCloudStorageProvider : ICloudStorageProvider
    {
        /// <summary>Canonical provider name - also the Google OIDC authentication scheme name.</summary>
        public const string ProviderName = "Google";

        private const string DriveFileScope = "https://www.googleapis.com/auth/drive.file";

        private readonly IGoogleAuthProvider _googleAuth;
        private readonly IOptionsMonitor<Model.Configuration> _config;
        private readonly IOptionsMonitor<Model.GoogleCloudServiceConfiguration> _googleServiceConfig;
        private readonly HttpClient _httpClient;
        private readonly ILogger<GoogleCloudStorageProvider> _logger;

        public GoogleCloudStorageProvider(
            IGoogleAuthProvider googleAuth,
            IOptionsMonitor<Model.Configuration> config,
            IOptionsMonitor<Model.GoogleCloudServiceConfiguration> googleServiceConfig,
            HttpClient httpClient,
            ILogger<GoogleCloudStorageProvider> logger)
        {
            _googleAuth = googleAuth;
            _config = config;
            _googleServiceConfig = googleServiceConfig;
            _httpClient = httpClient;
            _logger = logger;
        }

        public string Name => ProviderName;
        public string DisplayName => "Google";
        public string RequiredScope => DriveFileScope;

        public bool IsConfigured =>
            !string.IsNullOrWhiteSpace(_googleServiceConfig.CurrentValue.ClientId) &&
            !string.IsNullOrWhiteSpace(_googleServiceConfig.CurrentValue.ClientSecret);

        public async Task<string?> GetAmbientAccessTokenAsync()
        {
            try
            {
                var credential = await _googleAuth.GetCredentialAsync();
                return await credential.UnderlyingCredential.GetAccessTokenForRequestAsync();
            }
            catch (InvalidOperationException)
            {
                // GoogleAuthProvider.GetCredentialAsync throws this when there's no authenticated cookie
                // session on the current request - expected and routine for bearer-token API callers
                // (WalletController etc.), which have no ambient cookie session at all. The interface
                // contract is to return null here, matching MicrosoftCloudStorageProvider's equivalent
                // (a plain HttpContext.GetTokenAsync call, which returns null rather than throwing) - the
                // caller is responsible for treating a null ambient token as "no token available" (see
                // ICloudAccountRepository/SpendingLimitService, which both throw UnauthorizedAccessException
                // - mapped to 401 - only when neither an explicit nor an ambient token is available).
                return null;
            }
        }

        public async Task<bool> HasWriteAccessAsync(string accessToken)
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

                return grantedScopes.Contains(DriveFileScope, StringComparer.Ordinal);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to verify Google Drive write access; treating as not granted.");
                return false;
            }
        }

        public async Task<byte[]?> TryDownloadAsync(string fileName, string accessToken)
        {
            var service = CreateDriveService(accessToken);

            try
            {
                var folder = await FindFolderAsync(service);
                if (folder == null)
                {
                    return null;
                }

                var file = await FindFileAsync(service, folder.Id, fileName);
                if (file == null)
                {
                    return null;
                }

                var requestDownload = service.Files.Get(file.Id);
                var streamDownloadFile = new MemoryStream();
                await requestDownload.DownloadAsync(streamDownloadFile);
                streamDownloadFile.Position = 0;
                return streamDownloadFile.ToArray();
            }
            catch (Google.GoogleApiException ex) when (ex.HttpStatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                throw new UnauthorizedAccessException("Google Drive access denied. The access token may be expired or invalid.", ex);
            }
        }

        public async Task UploadAsync(string fileName, byte[] content, string accessToken)
        {
            var service = CreateDriveService(accessToken);

            try
            {
                var folder = await FindFolderAsync(service) ?? await CreateFolderAsync(service);

                var fileMetadata = new Google.Apis.Drive.v3.Data.File
                {
                    Name = fileName,
                    MimeType = "text/plain",
                    Parents = new List<string> { folder.Id }
                };

                using var stream = new MemoryStream(content);
                var request = service.Files.Create(fileMetadata, stream, "text/plain");
                request.Fields = "id, name";

                var result = await request.UploadAsync();
                if (result.Status != Google.Apis.Upload.UploadStatus.Completed)
                {
                    throw new Exception("File upload failed: " + result.Exception?.Message);
                }
            }
            catch (Google.GoogleApiException ex) when (ex.HttpStatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                throw new UnauthorizedAccessException("Google Drive access denied. The access token may be expired or invalid.", ex);
            }
        }

        /// <summary>Escapes a value for safe interpolation into a Google Drive API <c>q</c> search string.</summary>
        private static string EscapeDriveQueryValue(string value) => value.Replace("\\", "\\\\").Replace("'", "\\'");

        private DriveService CreateDriveService(string accessToken)
        {
            var credential = GoogleCredential.FromAccessToken(accessToken);
            return new DriveService(new BaseClientService.Initializer
            {
                HttpClientInitializer = credential,
                ApplicationName = _config.CurrentValue.ApplicationName,
            });
        }

        private async Task<Google.Apis.Drive.v3.Data.File?> FindFolderAsync(DriveService service)
        {
            var folderName = _config.CurrentValue.StorageFolderName;
            var folderRequest = service.Files.List();
            folderRequest.Q = $"mimeType = 'application/vnd.google-apps.folder' and name = '{EscapeDriveQueryValue(folderName)}' and trashed = false";
            folderRequest.Fields = "files(id, name)";
            var folderResult = await folderRequest.ExecuteAsync();
            return folderResult.Files?.FirstOrDefault();
        }

        private async Task<Google.Apis.Drive.v3.Data.File> CreateFolderAsync(DriveService service)
        {
            var folderMetadata = new Google.Apis.Drive.v3.Data.File
            {
                Name = _config.CurrentValue.StorageFolderName,
                MimeType = "application/vnd.google-apps.folder"
            };

            var folderCreateRequest = service.Files.Create(folderMetadata);
            folderCreateRequest.Fields = "id";
            return await folderCreateRequest.ExecuteAsync();
        }

        private async Task<Google.Apis.Drive.v3.Data.File?> FindFileAsync(DriveService service, string folderId, string fileName)
        {
            var fileCheckRequest = service.Files.List();
            fileCheckRequest.Q = $"name = '{EscapeDriveQueryValue(fileName)}' and '{EscapeDriveQueryValue(folderId)}' in parents and trashed = false";
            fileCheckRequest.Fields = "files(id, name)";
            var existingFiles = await fileCheckRequest.ExecuteAsync();
            return existingFiles.Files?.FirstOrDefault();
        }
    }
}
