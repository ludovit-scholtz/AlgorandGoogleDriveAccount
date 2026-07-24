using Algorand.Algod.Model;
using AlgorandGoogleDriveAccount.Helper;
using Google.Apis.Auth.AspNetCore3;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Drive.v3;
using Google.Apis.Services;
using Microsoft.Extensions.Options;
using System.Security.Cryptography;
using System.Text;

namespace AlgorandGoogleDriveAccount.Repository
{
    public class GoogleDriveRepository
    {
        private readonly IGoogleAuthProvider _auth;
        private readonly IOptionsMonitor<Model.Configuration> _config;
        private readonly IOptionsMonitor<Model.AesOptions> _aes;
        private readonly ILogger<GoogleDriveRepository> _logger;

        public GoogleDriveRepository(
            IGoogleAuthProvider auth,
            IOptionsMonitor<Model.Configuration> config,
            IOptionsMonitor<Model.AesOptions> aes,
            ILogger<GoogleDriveRepository> logger
            )
        {
            _auth = auth;
            _config = config;
            _aes = aes;
            _logger = logger;
        }

        /// <summary>Escapes a value for safe interpolation into a Google Drive API <c>q</c> search string.</summary>
        private static string EscapeDriveQueryValue(string value) => value.Replace("\\", "\\\\").Replace("'", "\\'");
        public async Task<Account> LoadAccount(string email, int slot, GoogleCredential? googleCredential = null)
        {
            try
            {
                var cred = googleCredential ?? await _auth.GetCredentialAsync();

                var service = new DriveService(new BaseClientService.Initializer
                {
                    HttpClientInitializer = cred,
                    ApplicationName = _config.CurrentValue.ApplicationName,
                });

                string folderName = _config.CurrentValue.StorageFolderName;
                string fileName = _config.CurrentValue.StorageFileName;

                var aesid = AesEncryptionHelper.MakeAesId(_aes.CurrentValue);

                fileName = fileName.Replace("%AESID%", aesid);

                // Try to find the folder "Biatec"
                var folderRequest = service.Files.List();
                folderRequest.Q = $"mimeType = 'application/vnd.google-apps.folder' and name = '{EscapeDriveQueryValue(folderName)}' and trashed = false";
                folderRequest.Fields = "files(id, name)";

                try
                {
                    var folderResult = await folderRequest.ExecuteAsync();

                    var folder = folderResult.Files?.FirstOrDefault();
                    if (folder == null)
                    {
                        // Create the folder if not found
                        var folderMetadata = new Google.Apis.Drive.v3.Data.File
                        {
                            Name = folderName,
                            MimeType = "application/vnd.google-apps.folder"
                        };

                        var folderCreateRequest = service.Files.Create(folderMetadata);
                        folderCreateRequest.Fields = "id";
                        folder = await folderCreateRequest.ExecuteAsync();
                    }

                    // Check if file with same name already exists in that folder
                    var fileCheckRequest = service.Files.List();
                    fileCheckRequest.Q = $"name = '{EscapeDriveQueryValue(fileName)}' and '{EscapeDriveQueryValue(folder.Id)}' in parents and trashed = false";
                    fileCheckRequest.Fields = "files(id, name)";
                    var existingFiles = await fileCheckRequest.ExecuteAsync();

                    if (existingFiles.Files == null || !existingFiles.Files.Any())
                    {
                        // Prepare file metadata
                        var fileMetadata = new Google.Apis.Drive.v3.Data.File
                        {
                            Name = fileName,
                            MimeType = "text/plain",
                            Parents = new List<string> { folder.Id }
                        };

                        var newAccount = new Account();
                        var encryptedData = AesEncryptionHelper.Encrypt(Encoding.UTF8.GetBytes(newAccount.ToMnemonic()), Convert.FromBase64String(_aes.CurrentValue.Key), Convert.FromBase64String(_aes.CurrentValue.IV), email);

                        // File content
                        var stream = new MemoryStream(encryptedData);

                        // Upload request
                        var request = service.Files.Create(fileMetadata, stream, "text/plain");
                        request.Fields = "id, name";

                        var result = await request.UploadAsync();

                        if (result.Status != Google.Apis.Upload.UploadStatus.Completed)
                        {
                            throw new Exception("File upload failed: " + result.Exception?.Message);
                        }
                        existingFiles = await fileCheckRequest.ExecuteAsync();

                        if (existingFiles.Files == null || !existingFiles.Files.Any())
                        {
                            throw new Exception("File upload failed, file not found after upload.");
                        }
                    }

                    var file = existingFiles.Files?.FirstOrDefault() ?? throw new Exception("File not found after upload.");

                    var requestDownload = service.Files.Get(file.Id);
                    var streamDownloadFile = new MemoryStream();
                    requestDownload.MediaDownloader.ProgressChanged += progress =>
                    {
                        // Optional: log download progress
                    };

                    await requestDownload.DownloadAsync(streamDownloadFile);
                    streamDownloadFile.Position = 0;
                    var fileContent = streamDownloadFile.ToArray();

                    try
                    {
                        var decryptedData = AesEncryptionHelper.Decrypt(fileContent, Convert.FromBase64String(_aes.CurrentValue.Key), Convert.FromBase64String(_aes.CurrentValue.IV), email);
                        var account = AlgorandARC76AccountDotNet.ARC76.GetEmailAccount(email, Encoding.UTF8.GetString(decryptedData), slot);
                        return account;
                    }
                    catch (CryptographicException cryptoEx)
                    {
                        // Log full detail server-side only - the caller-facing message must not leak email,
                        // file size, or raw cryptographic exception text (an information-disclosure /
                        // padding-oracle amplifier).
                        _logger.LogError(cryptoEx, "Decryption failed for email {Email}. File size: {FileSize} bytes.", email, fileContent.Length);
                        throw new InvalidOperationException("Unable to load the account. Please try re-pairing the device.");
                    }
                    catch (Exception decryptEx)
                    {
                        _logger.LogError(decryptEx, "Failed to decrypt account data for email {Email}. File size: {FileSize} bytes.", email, fileContent.Length);
                        throw new InvalidOperationException("Unable to load the account. Please try re-pairing the device.");
                    }
                }
                catch (Google.GoogleApiException ex) when (ex.HttpStatusCode == System.Net.HttpStatusCode.Unauthorized)
                {
                    throw new UnauthorizedAccessException("Google Drive access denied. The access token may be expired or invalid.", ex);
                }
            }
            catch (UnauthorizedAccessException)
            {
                // Re-throw authorization exceptions as-is
                throw;
            }
            catch (InvalidOperationException)
            {
                // Already sanitized above - re-throw as-is.
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading account from Google Drive for email {Email}", email);
                throw new InvalidOperationException("Error loading account from Google Drive.");
            }
        }
    }
}
