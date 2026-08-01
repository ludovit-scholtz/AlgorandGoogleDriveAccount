namespace BiatecOIDC.BusinessLogic
{
    /// <summary>Which provider a pending/linked backup request targets, resolved by <see cref="IVaultBackupService"/>.</summary>
    /// <param name="Email">The Biatec user this backup belongs to.</param>
    /// <param name="TargetProvider">The cloud provider name (see <c>ICloudStorageProvider.Name</c>) being linked as a backup destination.</param>
    public sealed record PendingVaultBackup(string Email, string TargetProvider);

    /// <summary>
    /// Explicit, user-triggered copy of a user's encrypted seed vault from their primary cloud provider to a
    /// second one they separately authorize - so losing access to one cloud account (a ban, forgotten
    /// credentials) doesn't mean losing the keys. Never automatic - nothing here runs unless the user starts
    /// it, and the target provider's access token is used exactly once (to perform the copy) and never cached
    /// or persisted afterwards.
    /// </summary>
    /// <remarks>
    /// Deliberately does not use the normal ASP.NET Core <c>Challenge()</c>/OIDC-scheme sign-in flow to link
    /// the second provider - that would re-fire the scheme's <c>OnTokenValidated</c> handler and overwrite the
    /// user's real <c>biatec_idp</c> cookie claim. Instead it drives a manual OAuth2 authorization-code round
    /// trip via <c>ICloudStorageProvider.BuildAuthorizationUrl</c>/<c>ExchangeAuthorizationCodeAsync</c>,
    /// entirely outside the cookie-authentication system.
    /// </remarks>
    public interface IVaultBackupService
    {
        /// <summary>
        /// Begins a backup link: generates an opaque, short-lived <c>linkId</c> correlating the rest of the
        /// flow. Throws <see cref="InvalidOperationException"/> if <paramref name="targetProvider"/> is the
        /// same as <paramref name="primaryProvider"/> (backing up to the same provider is meaningless) or
        /// isn't a recognized/configured provider.
        /// </summary>
        Task<string> StartAsync(string email, string primaryProvider, string targetProvider);

        /// <summary>Looks up a pending (not yet OAuth-completed) backup link by id, or <c>null</c> if it doesn't exist/expired.</summary>
        Task<PendingVaultBackup?> GetPendingAsync(string linkId);

        /// <summary>
        /// Completes the OAuth round trip: exchanges <paramref name="code"/> for an access token via the
        /// pending link's target provider, verifies it actually grants storage-write access, and - if so -
        /// caches that token (short-lived, one-shot) for <see cref="CompleteAsync"/> to consume. Returns an
        /// error message (never throws) if the pending link is gone/expired, the code exchange fails, or
        /// write access isn't granted.
        /// </summary>
        Task<(bool Success, string? Error)> HandleCallbackAsync(string linkId, string code, string redirectUri);

        /// <summary>
        /// Performs the actual copy: downloads the caller's current encrypted vault file from
        /// <paramref name="primaryProvider"/> (using their already-authenticated
        /// <paramref name="primaryAccessToken"/>) and uploads the identical bytes to the linked target
        /// provider under the same file name - no re-encryption needed, since both live under the same AES
        /// key ring regardless of storage backend. Consumes (deletes) the one-shot linked record either way.
        /// Returns an error message (never throws) if the link is missing/expired/doesn't belong to
        /// <paramref name="email"/>, or the copy itself fails.
        /// </summary>
        Task<(bool Success, string? Error)> CompleteAsync(string email, string primaryProvider, string? primaryAccessToken, string linkId);
    }
}
