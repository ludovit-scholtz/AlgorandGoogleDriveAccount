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
