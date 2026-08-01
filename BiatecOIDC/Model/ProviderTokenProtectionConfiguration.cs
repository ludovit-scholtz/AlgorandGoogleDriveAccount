using BiatecSelfCustodyCore.Model;

namespace BiatecOIDC.Model
{
    /// <summary>
    /// Bound from the <c>ProviderTokenProtection</c> configuration section. A dedicated, rotatable AES-256 key
    /// ring used only to encrypt the caller's Google/Microsoft access/refresh token when cached inside a
    /// Biatec access/refresh token (see <c>BusinessLogic.IProviderAccessTokenProtector</c>) - deliberately
    /// <em>not</em> the same key ring as <c>AesOptions</c> (which protects the self-custody account file), so
    /// the two secrets can be rotated independently and a leak of one doesn't automatically compromise
    /// the other. Same shape as <c>AesOptions</c> (<see cref="IAesKeyRingConfiguration"/>) for consistency; see
    /// <c>BiatecOIDC/OIDC_INTEGRATION_GUIDE.md</c>'s "Provider access token caching" section for the full
    /// threat-model writeup and key-generation/rotation instructions.
    /// </summary>
    public class ProviderTokenProtectionConfiguration : IAesKeyRingConfiguration
    {
        /// <inheritdoc />
        public string ActiveKeyId { get; set; } = string.Empty;

        /// <inheritdoc />
        public List<AesKeyRingEntry> Keys { get; set; } = new();
    }
}
