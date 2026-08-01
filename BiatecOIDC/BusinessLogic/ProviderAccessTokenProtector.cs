using System.Text;
using BiatecOIDC.Model;
using BiatecSelfCustodyCore.Helper;
using Microsoft.Extensions.Options;

namespace BiatecOIDC.BusinessLogic
{
    /// <inheritdoc cref="IProviderAccessTokenProtector"/>
    /// <remarks>
    /// Reuses <see cref="AesEncryptionHelper"/>'s authenticated AES-256-GCM format (random per-encryption
    /// salt/nonce, HKDF-derived per-encryption key, tamper-evident) - the exact same proven, tested code
    /// path the self-custody account file is encrypted with - but keyed by
    /// <see cref="ProviderTokenProtectionConfiguration"/>, a dedicated secret never shared with
    /// <c>AesOptions</c>. Binding the derivation to the caller's email (same parameter
    /// <see cref="AesEncryptionHelper.Encrypt"/> uses for the account file) means a ciphertext produced for
    /// one user can never be decrypted under a different user's email, even under the same key.
    /// </remarks>
    public sealed class ProviderAccessTokenProtector : IProviderAccessTokenProtector
    {
        /// <summary>The access-token claim type the encrypted provider token is stored under.</summary>
        public const string ClaimType = "provider_token";

        private readonly IOptionsMonitor<ProviderTokenProtectionConfiguration> _config;
        private readonly ILogger<ProviderAccessTokenProtector> _logger;

        public ProviderAccessTokenProtector(IOptionsMonitor<ProviderTokenProtectionConfiguration> config, ILogger<ProviderAccessTokenProtector> logger)
        {
            _config = config;
            _logger = logger;
        }

        public string? Protect(string providerAccessToken, string email)
        {
            if (string.IsNullOrWhiteSpace(providerAccessToken))
            {
                return null;
            }

            if (!TryGetKeyMaterial(out var key, out var iv))
            {
                return null;
            }

            try
            {
                var plaintext = Encoding.UTF8.GetBytes(providerAccessToken);
                var encrypted = AesEncryptionHelper.Encrypt(plaintext, key, iv, email);
                return Convert.ToBase64String(encrypted);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unable to encrypt provider access token for caching; caller will need to supply their own token explicitly.");
                return null;
            }
        }

        public string? Unprotect(string? protectedToken, string email)
        {
            if (string.IsNullOrWhiteSpace(protectedToken))
            {
                return null;
            }

            if (!TryGetKeyMaterial(out var key, out var iv))
            {
                return null;
            }

            try
            {
                var cipherData = Convert.FromBase64String(protectedToken);
                var plaintext = AesEncryptionHelper.Decrypt(cipherData, key, iv, email);
                return Encoding.UTF8.GetString(plaintext);
            }
            catch (Exception ex)
            {
                // Expected in benign cases too (key rotated since the token was cached, clock/email
                // mismatch) - never surfaced as an error to the caller, just treated as "not cached".
                _logger.LogWarning(ex, "Unable to decrypt cached provider access token; treating as not cached.");
                return null;
            }
        }

        private bool TryGetKeyMaterial(out byte[] key, out byte[] iv)
        {
            key = Array.Empty<byte>();
            iv = Array.Empty<byte>();

            var current = _config.CurrentValue;
            if (string.IsNullOrWhiteSpace(current.Key) || string.IsNullOrWhiteSpace(current.IV))
            {
                _logger.LogWarning("ProviderTokenProtection:Key/IV is not configured - provider access token caching is disabled; callers must supply their own token explicitly.");
                return false;
            }

            try
            {
                key = Convert.FromBase64String(current.Key);
                iv = Convert.FromBase64String(current.IV);
            }
            catch (FormatException ex)
            {
                _logger.LogError(ex, "ProviderTokenProtection:Key/IV is not valid base64 - provider access token caching is disabled.");
                return false;
            }

            if (key.Length != 32 || iv.Length != 16)
            {
                _logger.LogError("ProviderTokenProtection:Key must decode to 32 bytes and IV to 16 bytes - provider access token caching is disabled.");
                return false;
            }

            return true;
        }
    }
}
