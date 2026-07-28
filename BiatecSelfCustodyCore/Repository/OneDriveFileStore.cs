using System.Net;
using System.Net.Http.Headers;

namespace BiatecSelfCustodyCore.Repository
{
    /// <summary>
    /// Stores the account file in the user's OneDrive "app folder" (Microsoft Graph's
    /// <c>/me/drive/special/approot</c> special folder) - the OneDrive equivalent of Google Drive's
    /// <c>drive.file</c> scope: the app can only ever see files inside its own isolated folder, never
    /// browse the rest of the user's OneDrive. Requires the <c>Files.ReadWrite.AppFolder</c> delegated
    /// Graph permission (see <c>BiatecOIDC/ENTRA_SETUP_GUIDE.md</c>).
    /// </summary>
    public class OneDriveFileStore
    {
        private const string GraphBaseUrl = "https://graph.microsoft.com/v1.0";

        private readonly HttpClient _httpClient;

        public OneDriveFileStore(HttpClient httpClient)
        {
            _httpClient = httpClient;
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
