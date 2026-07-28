namespace BiatecSelfCustodyCore.Providers
{
    /// <summary>
    /// A cloud identity/storage backend the self-custody account file can live in (Google Drive,
    /// OneDrive, and any future provider). This is the single extension point for adding a new
    /// provider: implement this interface, add one <c>AuthenticationBuilder</c> extension method
    /// to wire up its OIDC scheme + DI registration (see <c>CloudStorageProviderAuthExtensions</c>),
    /// and register it in each app's <c>Program.cs</c> - no other shared-lib type needs to change.
    /// </summary>
    public interface ICloudStorageProvider
    {
        /// <summary>
        /// Canonical provider identifier. By convention this is also the authentication scheme
        /// name registered for it (<c>Challenge(properties, provider.Name)</c>), and the value
        /// persisted as e.g. <c>PairedDeviceInfo.Provider</c>.
        /// </summary>
        string Name { get; }

        /// <summary>Human-readable label for provider-picker UIs (e.g. "Google").</summary>
        string DisplayName { get; }

        /// <summary>
        /// The OAuth/OIDC scope that grants storage-write access for this provider (e.g. Google's
        /// <c>drive.file</c>, Microsoft Graph's <c>Files.ReadWrite.AppFolder</c>). Used both in the
        /// initial sign-in scope list (<c>Program.cs</c>) and when re-requesting it via incremental
        /// consent (<see cref="BusinessLogic.OpenIdConnectIncrementalAuth"/>) - single source of
        /// truth so a new provider only defines this once.
        /// </summary>
        string RequiredScope { get; }

        /// <summary>Downloads <paramref name="fileName"/>'s content, or <c>null</c> if it doesn't exist yet.</summary>
        Task<byte[]?> TryDownloadAsync(string fileName, string accessToken);

        /// <summary>Creates or overwrites <paramref name="fileName"/> with <paramref name="content"/>.</summary>
        Task UploadAsync(string fileName, byte[] content, string accessToken);

        /// <summary>
        /// Whether <paramref name="accessToken"/> actually grants this provider's storage-write
        /// permission (e.g. Google's <c>drive.file</c> scope, Microsoft Graph's
        /// <c>Files.ReadWrite.AppFolder</c>) - checked before finalizing a sign-in/pairing so a
        /// session is never completed against a token that can't read/write the account file.
        /// </summary>
        Task<bool> HasWriteAccessAsync(string accessToken);

        /// <summary>
        /// The current signed-in user's access token for this provider, resolved from the ambient
        /// <c>HttpContext</c> (the cookie-session flows in <c>DriveController</c>/
        /// <c>JwtIssuerService</c>, which have no explicit token to hand in - unlike the
        /// device-pairing path, which passes a Redis-stored token directly). Returns <c>null</c> if
        /// there isn't one.
        /// </summary>
        Task<string?> GetAmbientAccessTokenAsync();
    }
}
