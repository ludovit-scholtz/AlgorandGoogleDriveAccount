using Google.Apis.Auth.OAuth2;
using Google.Apis.Drive.v3;
using Google.Apis.Services;
using Microsoft.Extensions.Options;

namespace BiatecSelfCustodyCore.Repository
{
    /// <summary>
    /// Stores the account file in a Google Drive folder (created on first use) owned by the app, named per
    /// <see cref="Model.Configuration.StorageFolderName"/>. Takes a <see cref="GoogleCredential"/> directly
    /// (rather than a raw bearer string) so callers can either build one from an explicit access token
    /// (device-pairing path) or hand in the ambient, auto-refreshing credential from
    /// <c>IGoogleAuthProvider.GetCredentialAsync()</c> (cookie-session path) - matching how the Google API
    /// client library is meant to be used.
    /// </summary>
    public class GoogleDriveFileStore
    {
        private readonly IOptionsMonitor<Model.Configuration> _config;

        public GoogleDriveFileStore(IOptionsMonitor<Model.Configuration> config)
        {
            _config = config;
        }

        /// <summary>Escapes a value for safe interpolation into a Google Drive API <c>q</c> search string.</summary>
        private static string EscapeDriveQueryValue(string value) => value.Replace("\\", "\\\\").Replace("'", "\\'");

        public async Task<byte[]?> TryDownloadAsync(string fileName, GoogleCredential credential)
        {
            var service = CreateDriveService(credential);

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

        public async Task UploadAsync(string fileName, byte[] content, GoogleCredential credential)
        {
            var service = CreateDriveService(credential);

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

        private DriveService CreateDriveService(GoogleCredential credential)
        {
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
