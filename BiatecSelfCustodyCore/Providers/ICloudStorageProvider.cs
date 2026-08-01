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
        /// Whether this provider's own app registration (client id/secret, etc.) is actually filled
        /// in for the running environment - a provider can be registered in DI (so its scheme exists
        /// and it always shows up in <see cref="ICloudStorageProviderCatalog.All"/>) while still
        /// having no usable credentials (e.g. Microsoft Entra not set up for local dev). Picker UIs
        /// should filter on this so they never render a button for a sign-in that can't work.
        /// </summary>
        bool IsConfigured { get; }

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
        /// Best-effort delete of <paramref name="fileName"/> - never throws, including when the file doesn't
        /// exist. Used only to clean up a stale file just after its contents were migrated to a new AES key
        /// generation (see <c>EncryptedKeyRingFileStore</c>) - a failed delete just leaves a harmless
        /// orphaned (still access-token-gated, still encrypted) file behind, so it's never worth failing the
        /// caller's request over.
        /// </summary>
        Task DeleteAsync(string fileName, string accessToken);

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

        /// <summary>
        /// The current signed-in user's long-lived refresh token for this provider, resolved from the
        /// ambient <c>HttpContext</c> the same way as <see cref="GetAmbientAccessTokenAsync"/>. Used by
        /// <c>BiatecOIDC</c>'s wallet API to cache a way to renew the provider access token embedded in an
        /// issued Biatec token (see <c>ProviderAccessTokenProtector</c>'s <c>RefreshClaimType</c> and
        /// <c>OIDC_INTEGRATION_GUIDE.md</c>'s "Provider access token caching" section) once it expires -
        /// without that, a caller would have to go through a fresh interactive sign-in every time the
        /// short-lived provider access token expires, even while their Biatec token is still valid. Returns
        /// <c>null</c> if there isn't one (no ambient session, or the provider never issued one).
        /// </summary>
        Task<string?> GetAmbientRefreshTokenAsync();

        /// <summary>
        /// Exchanges <paramref name="refreshToken"/> for a fresh access token via this provider's own OAuth
        /// token endpoint. Returns <c>null</c> (never throws) if the refresh token is invalid, expired, or
        /// revoked, or if the request otherwise fails - callers should treat that the same as "no token
        /// available" and fall back to requiring a fresh interactive sign-in.
        /// </summary>
        Task<ProviderTokenRefreshResult?> RefreshAccessTokenAsync(string refreshToken);

        /// <summary>
        /// Builds this provider's own OAuth2 authorization URL (requesting exactly <see cref="RequiredScope"/>,
        /// nothing else) for a manual, out-of-band consent round trip - used by <c>BiatecOIDC</c>'s
        /// cross-cloud vault backup flow to link a *second* provider without disturbing the caller's
        /// existing signed-in session (deliberately not routed through this provider's normal ASP.NET Core
        /// authentication scheme/<c>Challenge()</c>, which would re-fire that scheme's cookie sign-in).
        /// </summary>
        /// <param name="redirectUri">Must exactly match the redirect URI used in <see cref="ExchangeAuthorizationCodeAsync"/>.</param>
        /// <param name="state">Opaque value round-tripped back unchanged - used to correlate the callback with the request that started it.</param>
        string BuildAuthorizationUrl(string redirectUri, string state);

        /// <summary>
        /// Exchanges an authorization <paramref name="code"/> (obtained via the URL from
        /// <see cref="BuildAuthorizationUrl"/>) for an access token, via this provider's own OAuth token
        /// endpoint. Returns <c>null</c> (never throws) if the code is invalid/expired/already used, or the
        /// request otherwise fails.
        /// </summary>
        /// <param name="redirectUri">Must exactly match the redirect URI used in <see cref="BuildAuthorizationUrl"/>.</param>
        Task<string?> ExchangeAuthorizationCodeAsync(string code, string redirectUri);
    }
}
