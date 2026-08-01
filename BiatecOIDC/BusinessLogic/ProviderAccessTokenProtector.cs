using System.Text;
using BiatecOIDC.Model;
using BiatecSelfCustodyCore.Helper;
using BiatecSelfCustodyCore.Model;
using Microsoft.Extensions.Options;

namespace BiatecOIDC.BusinessLogic
{
    /// <inheritdoc cref="IProviderAccessTokenProtector"/>
    /// <remarks>
    /// Reuses <see cref="AesEncryptionHelper"/>'s authenticated AES-256-GCM format (random per-encryption
    /// salt/nonce, HKDF-derived per-encryption key, tamper-evident) - the exact same proven, tested code
    /// path the self-custody account file is encrypted with - but keyed by its own rotatable key ring
    /// (<see cref="ProviderTokenProtectionConfiguration"/>, never shared with <c>AesOptions</c>). Binding the
    /// derivation to the caller's email (same parameter <see cref="AesEncryptionHelper.Encrypt"/> uses for
    /// the account file) means a ciphertext produced for one user can never be decrypted under a different
    /// user's email, even under the same key.
    /// </remarks>
    public sealed class ProviderAccessTokenProtector : IProviderAccessTokenProtector
    {
        /// <summary>The access-token claim type the encrypted provider access token is stored under.</summary>
        public const string ClaimType = "provider_token";

        /// <summary>
        /// The access-token claim type the encrypted provider refresh token is stored under - used to
        /// renew <see cref="ClaimType"/> once the cached provider access token expires, both when the
        /// caller renews its own Biatec refresh token (<c>JwtIssuerService.ExchangeTokenAsync</c>'s
        /// <c>refresh_token</c> grant) and, opportunistically, mid-lifetime of a still-valid Biatec access
        /// token (<c>WalletController</c>). Protected/unprotected with the same key ring and per-email
        /// binding as <see cref="ClaimType"/> - it's the same trust boundary, just a longer-lived credential.
        /// </summary>
        public const string RefreshClaimType = "provider_refresh_token";

        private readonly IOptionsMonitor<ProviderTokenProtectionConfiguration> _config;
        private readonly ILogger<ProviderAccessTokenProtector> _logger;

        public ProviderAccessTokenProtector(IOptionsMonitor<ProviderTokenProtectionConfiguration> config, IHostEnvironment environment, ILogger<ProviderAccessTokenProtector> logger)
        {
            _config = config;
            _logger = logger;

            // No wallet endpoint accepts a caller-supplied provider access token anymore - the
            // provider_token claim (decrypted with this key ring) is the *only* way any of them can be
            // resolved. A missing/invalid active key here no longer degrades gracefully to "caller must
            // pass their own token" - it means the wallet API cannot function *at all*. Fail loudly outside
            // Development, matching JwtIssuerService.LoadOrCreateSigningKey's precedent for equally
            // load-bearing secrets, rather than leaving every wallet call silently returning 401 with no
            // indication why. Also fails fast if the resolved active key is the known placeholder value
            // committed in k8s/main/conf-oidc/appsettings.json (security audit finding R-023/P-01).
            if (!environment.IsDevelopment())
            {
                var activeKey = AesKeyRingResolver.GetActiveKey(config.CurrentValue, "ProviderTokenProtection");
                AesKeyRingResolver.EnsureActiveKeyIsNotKnownPlaceholder(activeKey, "ProviderTokenProtection");
            }
        }

        public string? Protect(string providerAccessToken, string email)
        {
            if (string.IsNullOrWhiteSpace(providerAccessToken))
            {
                return null;
            }

            AesKeyRingEntry activeKey;
            try
            {
                activeKey = AesKeyRingResolver.GetActiveKey(_config.CurrentValue, "ProviderTokenProtection");
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogError(ex, "Unable to resolve the active ProviderTokenProtection key; the wallet API will be unable to cache a provider token for this session.");
                return null;
            }

            try
            {
                var plaintext = Encoding.UTF8.GetBytes(providerAccessToken);
                var encrypted = AesEncryptionHelper.Encrypt(plaintext, AesKeyRingResolver.KeyBytes(activeKey), AesKeyRingResolver.IvBytes(activeKey), email);
                return Convert.ToBase64String(encrypted);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unable to encrypt provider access token for caching; the wallet API will be unable to resolve a provider token for this session.");
                return null;
            }
        }

        public string? Unprotect(string? protectedToken, string email)
        {
            if (string.IsNullOrWhiteSpace(protectedToken))
            {
                return null;
            }

            byte[] cipherData;
            try
            {
                cipherData = Convert.FromBase64String(protectedToken);
            }
            catch (FormatException)
            {
                return null;
            }

            // Tries the active key first, then every historical generation, in order. This is a safe blind
            // trial-decrypt (unlike a filename-addressable blob, a JWT claim has no way to indicate which
            // generation encrypted it) because this protector only ever writes the authenticated AES-GCM
            // format - a wrong key deterministically fails the auth-tag check, so there's no risk of
            // silently "succeeding" against the wrong key with garbage plaintext.
            foreach (var candidate in GetOrderedCandidateKeys())
            {
                try
                {
                    var plaintext = AesEncryptionHelper.Decrypt(cipherData, AesKeyRingResolver.KeyBytes(candidate), AesKeyRingResolver.IvBytes(candidate), email);
                    return Encoding.UTF8.GetString(plaintext);
                }
                catch (Exception)
                {
                    // Expected for every key generation except whichever one actually encrypted this
                    // ciphertext - move on to the next candidate.
                }
            }

            // Expected in benign cases too (every configured key generation that could have encrypted this
            // has since been retired, clock/email mismatch) - never surfaced as an error to the caller, just
            // treated as "not cached".
            _logger.LogWarning("Unable to decrypt cached provider access token under any configured AES key generation; treating as not cached.");
            return null;
        }

        /// <summary>Active key first (the common case once rotation has propagated), then every historical generation in declared order.</summary>
        private IEnumerable<AesKeyRingEntry> GetOrderedCandidateKeys()
        {
            AesKeyRingEntry? activeKey = null;
            try
            {
                activeKey = AesKeyRingResolver.GetActiveKey(_config.CurrentValue, "ProviderTokenProtection");
            }
            catch (InvalidOperationException)
            {
                // Fall through to historical keys - one of them might still successfully decrypt even if
                // the active key is currently misconfigured.
            }

            if (activeKey != null)
            {
                yield return activeKey;
            }

            foreach (var historical in AesKeyRingResolver.GetHistoricalKeys(_config.CurrentValue, _logger))
            {
                yield return historical;
            }
        }
    }
}
