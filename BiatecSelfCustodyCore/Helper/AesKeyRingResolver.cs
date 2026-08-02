using BiatecSelfCustodyCore.Model;
using Microsoft.Extensions.Logging;

namespace BiatecSelfCustodyCore.Helper
{
    /// <summary>
    /// Resolves the active/historical generations of an <see cref="IAesKeyRingConfiguration"/> (shared by
    /// <see cref="AesOptions"/> and <c>BiatecOIDC.Model.ProviderTokenProtectionConfiguration</c>), so both key
    /// rings validate and look up key material through one piece of code.
    /// </summary>
    public static class AesKeyRingResolver
    {
        /// <summary>
        /// Resolves the generation to use for all new encryption. Fails fast (throws
        /// <see cref="InvalidOperationException"/>) if <see cref="IAesKeyRingConfiguration.ActiveKeyId"/> is
        /// unset, doesn't match any <see cref="IAesKeyRingConfiguration.Keys"/> entry, or that entry's
        /// Key/IV isn't valid base64 of the required length - callers use this at construction time so a
        /// misconfigured active key is caught at startup, not as a wall of unexplained runtime failures.
        /// </summary>
        /// <param name="config">The key ring configuration (e.g. <c>AesOptions</c> or <c>ProviderTokenProtectionConfiguration</c>) to resolve the active key from.</param>
        /// <param name="configSectionName">Used only to make the exception message actionable (e.g. "AesOptions").</param>
        public static AesKeyRingEntry GetActiveKey(IAesKeyRingConfiguration config, string configSectionName)
        {
            if (string.IsNullOrWhiteSpace(config.ActiveKeyId))
            {
                throw new InvalidOperationException(
                    $"{configSectionName}:ActiveKeyId is not configured. Configure it to the KeyId of one of " +
                    $"{configSectionName}:Keys.");
            }

            var entry = config.Keys.FirstOrDefault(k => string.Equals(k.KeyId, config.ActiveKeyId, StringComparison.Ordinal));
            if (entry == null)
            {
                throw new InvalidOperationException(
                    $"{configSectionName}:ActiveKeyId '{config.ActiveKeyId}' does not match any KeyId in " +
                    $"{configSectionName}:Keys.");
            }

            ValidateKeyMaterial(entry, configSectionName);
            return entry;
        }

        /// <summary>
        /// Every configured generation except the active one, in declared order - used as ordered candidates
        /// when data can't be decrypted under the active key. An entry with invalid/unparsable Key/IV is
        /// skipped with a logged warning rather than thrown; a single bad historical entry shouldn't stop the
        /// active key (or other historical keys) from working.
        /// </summary>
        public static IReadOnlyList<AesKeyRingEntry> GetHistoricalKeys(IAesKeyRingConfiguration config, ILogger logger)
        {
            var historical = new List<AesKeyRingEntry>();
            foreach (var entry in config.Keys)
            {
                if (string.Equals(entry.KeyId, config.ActiveKeyId, StringComparison.Ordinal))
                {
                    continue;
                }

                try
                {
                    ValidateKeyMaterial(entry, "Keys");
                    historical.Add(entry);
                }
                catch (InvalidOperationException ex)
                {
                    logger.LogWarning(ex, "Skipping historical AES key generation {KeyId} - invalid key material.", entry.KeyId);
                }
            }

            return historical;
        }

        /// <summary>
        /// Byte-identical to the values committed in <c>k8s/main/conf-mcp/appsettings.json</c> and
        /// <c>k8s/main/conf-oidc/appsettings.json</c> (see security audit findings R-019/G-02 for
        /// <c>AesOptions</c> and R-023/P-01 for <c>ProviderTokenProtection</c>), and deliberately also used
        /// by this repository's own root <c>appsettings.json</c> files as a shared, convenience example key
        /// for local development - so these must never be treated as acceptable *live* key material, but
        /// also must never be rejected unconditionally (that would break local development, which uses
        /// them by design). See <see cref="EnsureActiveKeyIsNotKnownPlaceholder"/>.
        /// </summary>
        private static readonly HashSet<(string Key, string IV)> KnownPlaceholderKeyMaterial = new()
        {
            // Current placeholder: all-zero bytes, deliberately unmistakable - used going forward in
            // k8s/main/conf-*/appsettings.json and k8s/stage/conf-*-stage/appsettings.json.
            ("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=", "AAAAAAAAAAAAAAAAAAAAAA=="),
            // Historical: the syntactically-real-looking value those same files committed before this fix
            // (findings R-019/G-02, R-023/P-01) - kept here too in case an older ConfigMap/Secret revision
            // is ever rolled back to it.
            ("dFskKJD/h4YpQWhbNOQmmvRyuJ+zMSBbg+v3Jg5LvQw=", "aNfjtgsymNYAqxhzHU30XQ=="), // AesOptions
            ("g46fY8Nnr77edXDqCKP+d92nm8roYITklIVy4mGFE2w=", "T0Oc4SEMxUfljFeJEj8tfQ=="), // ProviderTokenProtection
        };

        /// <summary>
        /// Fails fast (throws <see cref="InvalidOperationException"/>) if <paramref name="activeKey"/>'s
        /// Key/IV byte-for-byte matches a known placeholder value that must never be used as live key
        /// material (<see cref="KnownPlaceholderKeyMaterial"/>). Callers must invoke this only from the same
        /// <c>!environment.IsDevelopment()</c> guard already used for the general <see cref="GetActiveKey"/>
        /// startup validation - never unconditionally, and never from <see cref="GetActiveKey"/>/
        /// <see cref="GetHistoricalKeys"/> themselves, since those run on every request (in every
        /// environment, including Development, which uses this exact value by design for local convenience).
        /// This closes the specific gap the second and third security audits identified: a deployment whose
        /// secret override is missing would previously start up successfully and silently serve production
        /// traffic under the publicly-committed key, since <see cref="GetActiveKey"/>'s existing validation
        /// only checks that the configured value is *syntactically* well-formed, not whether it's this
        /// specific known-bad value.
        /// </summary>
        public static void EnsureActiveKeyIsNotKnownPlaceholder(AesKeyRingEntry activeKey, string configSectionName)
        {
            if (KnownPlaceholderKeyMaterial.Contains((activeKey.Key, activeKey.IV)))
            {
                throw new InvalidOperationException(
                    $"{configSectionName}:Keys entry '{activeKey.KeyId}' (the active key) is a known placeholder " +
                    "value committed in source control and must never be used as live key material outside " +
                    "Development. Override it with a real, freshly-generated secret before starting.");
            }
        }

        /// <summary>Base64-decodes an already-validated entry's <see cref="AesKeyRingEntry.Key"/>.</summary>
        public static byte[] KeyBytes(AesKeyRingEntry entry) => Convert.FromBase64String(entry.Key);

        /// <summary>Base64-decodes an already-validated entry's <see cref="AesKeyRingEntry.IV"/>.</summary>
        public static byte[] IvBytes(AesKeyRingEntry entry) => Convert.FromBase64String(entry.IV);

        private static void ValidateKeyMaterial(AesKeyRingEntry entry, string configSectionName)
        {
            if (string.IsNullOrWhiteSpace(entry.Key) || string.IsNullOrWhiteSpace(entry.IV))
            {
                throw new InvalidOperationException($"{configSectionName} key generation '{entry.KeyId}' is missing Key/IV.");
            }

            byte[] key;
            byte[] iv;
            try
            {
                key = Convert.FromBase64String(entry.Key);
                iv = Convert.FromBase64String(entry.IV);
            }
            catch (FormatException ex)
            {
                throw new InvalidOperationException($"{configSectionName} key generation '{entry.KeyId}' Key/IV is not valid base64.", ex);
            }

            if (key.Length != 32 || iv.Length != 16)
            {
                throw new InvalidOperationException($"{configSectionName} key generation '{entry.KeyId}' must decode to a 32-byte Key and 16-byte IV.");
            }
        }
    }
}
