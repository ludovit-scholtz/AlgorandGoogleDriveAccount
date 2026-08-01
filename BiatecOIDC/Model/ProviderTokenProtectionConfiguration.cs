namespace BiatecOIDC.Model
{
    /// <summary>
    /// Bound from the <c>ProviderTokenProtection</c> configuration section. A dedicated AES-256 key/IV pair
    /// used only to encrypt the caller's Google/Microsoft access token when it's cached inside a Biatec
    /// access/refresh token (see <c>BusinessLogic.IProviderAccessTokenProtector</c>) - deliberately
    /// <em>not</em> the same key as <c>AesOptions</c> (which protects the self-custody account file), so
    /// the two secrets can be rotated independently and a leak of one doesn't automatically compromise
    /// the other. Same shape as <c>AesOptions</c> for consistency; see
    /// <c>BiatecOIDC/OIDC_INTEGRATION_GUIDE.md</c>'s "Provider access token caching" section for the full
    /// threat-model writeup and key-generation instructions.
    /// </summary>
    public class ProviderTokenProtectionConfiguration
    {
        /// <summary>Base64-encoded 32-byte AES-256 key.</summary>
        public string Key { get; set; } = string.Empty;

        /// <summary>
        /// Base64-encoded 16-byte IV. Kept only for parameter symmetry with
        /// <c>BiatecSelfCustodyCore.Helper.AesEncryptionHelper</c>'s signature - the current authenticated
        /// AES-GCM format this protector uses derives its own random per-encryption nonce and doesn't
        /// actually consume this value.
        /// </summary>
        public string IV { get; set; } = string.Empty;
    }
}
