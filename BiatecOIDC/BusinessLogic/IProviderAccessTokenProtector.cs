namespace BiatecOIDC.BusinessLogic
{
    /// <summary>
    /// Encrypts/decrypts the caller's Google/Microsoft access token so it can be safely cached inside a
    /// Biatec-issued access token (as the <c>provider_token</c> claim - see
    /// <see cref="ProviderAccessTokenProtector.ClaimType"/>) for the token's own lifetime, instead of every
    /// wallet-API caller having to separately manage and resend it. See
    /// <c>BiatecOIDC/OIDC_INTEGRATION_GUIDE.md</c>'s "Provider access token caching" section for the full
    /// design/threat-model writeup.
    /// </summary>
    public interface IProviderAccessTokenProtector
    {
        /// <summary>
        /// Encrypts <paramref name="providerAccessToken"/>, bound to <paramref name="email"/>. Returns
        /// <c>null</c> (never throws) if encryption isn't possible right now (e.g. the dedicated key isn't
        /// configured) - callers must treat that the same as "nothing to cache" and fall back to requiring
        /// the caller to supply their own token explicitly, never fail the whole request over it.
        /// </summary>
        string? Protect(string providerAccessToken, string email);

        /// <summary>
        /// Decrypts a value previously returned by <see cref="Protect"/>, bound to the same
        /// <paramref name="email"/> it was encrypted with. Returns <c>null</c> (never throws) if
        /// <paramref name="protectedToken"/> is null/blank, can't be decrypted (wrong key, tampered,
        /// wrong email), or the protection key isn't configured - callers must treat that the same as "no
        /// cached token available".
        /// </summary>
        string? Unprotect(string? protectedToken, string email);
    }
}
