using BiatecSelfCustodyCore.Model;
using BiatecSelfCustodyCore.Providers;
using Microsoft.Extensions.Logging;

namespace BiatecSelfCustodyCore.Helper
{
    /// <summary>
    /// Loads/saves an AES-GCM encrypted cloud-storage file whose name is derived from the AES key generation
    /// that encrypted it (the <c>%AESID%</c> placeholder - see <see cref="AesEncryptionHelper.MakeAesId"/>),
    /// transparently migrating a file found under a historical generation onto the active one the moment
    /// it's read. Shared by <c>CloudAccountRepository</c> (the self-custody account file) and
    /// <c>SpendingLimitService</c> (the spending-limit settings/ledger files) so both get identical,
    /// once-written rotation behavior instead of duplicating it.
    /// </summary>
    /// <remarks>
    /// Because each key generation's file lives under its own distinct name, rotation never needs to guess
    /// which key decrypts an existing blob - it just tries the active generation's file name first, then
    /// each historical generation's file name in turn, until one is found. The first one found is
    /// unambiguously known to be encrypted with that exact generation's key, so there's no blind multi-key
    /// trial-decryption (which would be unsafe against the legacy unauthenticated AES-CBC format
    /// <see cref="AesEncryptionHelper.Decrypt"/> still supports for very old files).
    /// </remarks>
    public static class EncryptedKeyRingFileStore
    {
        /// <summary>
        /// Downloads and decrypts the file named by <paramref name="fileNameTemplate"/> (with <c>%AESID%</c>
        /// substituted per key generation). Tries <paramref name="activeKey"/>'s file name first (the fast
        /// path once migration has happened); if not found, tries each of <paramref name="historicalKeys"/>'s
        /// file names in order. The first one found is decrypted with that exact generation's key, then
        /// immediately re-encrypted under <paramref name="activeKey"/> and re-uploaded under the active file
        /// name (and the stale historical file is best-effort deleted via
        /// <see cref="ICloudStorageProvider.DeleteAsync"/>), so the very next read hits the fast path.
        /// Returns <c>null</c> if no file exists under any known generation (genuinely new data).
        /// </summary>
        public static async Task<byte[]?> LoadAsync(
            ICloudStorageProvider provider,
            string accessToken,
            string fileNameTemplate,
            AesKeyRingEntry activeKey,
            IReadOnlyList<AesKeyRingEntry> historicalKeys,
            string email,
            ILogger logger)
        {
            var activeFileName = BuildFileName(fileNameTemplate, activeKey);
            var activeBytes = await provider.TryDownloadAsync(activeFileName, accessToken);
            if (activeBytes != null)
            {
                return AesEncryptionHelper.Decrypt(activeBytes, AesKeyRingResolver.KeyBytes(activeKey), AesKeyRingResolver.IvBytes(activeKey), email);
            }

            foreach (var historicalKey in historicalKeys)
            {
                var historicalFileName = BuildFileName(fileNameTemplate, historicalKey);
                var historicalBytes = await provider.TryDownloadAsync(historicalFileName, accessToken);
                if (historicalBytes == null)
                {
                    continue;
                }

                var plaintext = AesEncryptionHelper.Decrypt(historicalBytes, AesKeyRingResolver.KeyBytes(historicalKey), AesKeyRingResolver.IvBytes(historicalKey), email);

                logger.LogInformation(
                    "Migrating {FileName} from AES key generation {OldKeyId} to the active generation {ActiveKeyId}.",
                    historicalFileName, historicalKey.KeyId, activeKey.KeyId);

                var reEncrypted = AesEncryptionHelper.Encrypt(plaintext, AesKeyRingResolver.KeyBytes(activeKey), AesKeyRingResolver.IvBytes(activeKey), email);
                await provider.UploadAsync(activeFileName, reEncrypted, accessToken);
                await provider.DeleteAsync(historicalFileName, accessToken);

                return plaintext;
            }

            return null;
        }

        /// <summary>Encrypts <paramref name="plaintext"/> under <paramref name="activeKey"/> and uploads it under the active generation's file name - writes never touch historical generations.</summary>
        public static async Task SaveAsync(
            ICloudStorageProvider provider,
            string accessToken,
            string fileNameTemplate,
            AesKeyRingEntry activeKey,
            string email,
            byte[] plaintext)
        {
            var fileName = BuildFileName(fileNameTemplate, activeKey);
            var encrypted = AesEncryptionHelper.Encrypt(plaintext, AesKeyRingResolver.KeyBytes(activeKey), AesKeyRingResolver.IvBytes(activeKey), email);
            await provider.UploadAsync(fileName, encrypted, accessToken);
        }

        private static string BuildFileName(string fileNameTemplate, AesKeyRingEntry keyEntry)
        {
            var aesId = AesEncryptionHelper.MakeAesId(AesKeyRingResolver.KeyBytes(keyEntry), AesKeyRingResolver.IvBytes(keyEntry));
            return fileNameTemplate.Replace("%AESID%", aesId);
        }
    }
}
