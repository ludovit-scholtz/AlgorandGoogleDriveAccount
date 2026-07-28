using System.Security.Cryptography;
using System.Text;
using Algorand.Algod.Model;
using BiatecSelfCustodyCore.Helper;
using BiatecSelfCustodyCore.Model;
using BiatecSelfCustodyCore.Providers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BiatecSelfCustodyCore.Repository
{
    /// <summary>
    /// Owns the AES encrypt/decrypt + ARC76 account-derivation logic for the self-custody account
    /// file, once, regardless of which cloud provider it's stored with - resolves the provider from
    /// <see cref="ICloudStorageProviderCatalog"/> and only ever talks to it through
    /// <see cref="ICloudStorageProvider"/>, so adding a new provider never requires touching this class.
    /// </summary>
    public class CloudAccountRepository : ICloudAccountRepository
    {
        private readonly ICloudStorageProviderCatalog _catalog;
        private readonly IOptionsMonitor<Configuration> _config;
        private readonly IOptionsMonitor<AesOptions> _aes;
        private readonly ILogger<CloudAccountRepository> _logger;

        public CloudAccountRepository(
            ICloudStorageProviderCatalog catalog,
            IOptionsMonitor<Configuration> config,
            IOptionsMonitor<AesOptions> aes,
            ILogger<CloudAccountRepository> logger)
        {
            _catalog = catalog;
            _config = config;
            _aes = aes;
            _logger = logger;
        }

        public async Task<Account> LoadAccountAsync(string email, int slot, string provider, string? accessToken = null)
        {
            var storageProvider = _catalog.Resolve(provider);

            try
            {
                var token = accessToken ?? await storageProvider.GetAmbientAccessTokenAsync();
                if (string.IsNullOrEmpty(token))
                {
                    throw new UnauthorizedAccessException($"No {storageProvider.Name} access token available. Please sign in again.");
                }

                var fileName = BuildFileName();
                var existing = await storageProvider.TryDownloadAsync(fileName, token);

                byte[] mnemonicBytes;
                if (existing == null)
                {
                    var newAccount = new Account();
                    mnemonicBytes = Encoding.UTF8.GetBytes(newAccount.ToMnemonic());
                    var encrypted = AesEncryptionHelper.Encrypt(mnemonicBytes, AesKey(), AesIv(), email);
                    await storageProvider.UploadAsync(fileName, encrypted, token);
                }
                else
                {
                    try
                    {
                        mnemonicBytes = AesEncryptionHelper.Decrypt(existing, AesKey(), AesIv(), email);
                    }
                    catch (CryptographicException cryptoEx)
                    {
                        // Log full detail server-side only - the caller-facing message must not leak email,
                        // file size, or raw cryptographic exception text (an information-disclosure /
                        // padding-oracle amplifier).
                        _logger.LogError(cryptoEx, "Decryption failed for email {Email}. File size: {FileSize} bytes.", email, existing.Length);
                        throw new InvalidOperationException("Unable to load the account. Please try re-pairing the device.");
                    }
                    catch (Exception decryptEx)
                    {
                        _logger.LogError(decryptEx, "Failed to decrypt account data for email {Email}. File size: {FileSize} bytes.", email, existing.Length);
                        throw new InvalidOperationException("Unable to load the account. Please try re-pairing the device.");
                    }
                }

                return AlgorandARC76AccountDotNet.ARC76.GetEmailAccount(email, Encoding.UTF8.GetString(mnemonicBytes), slot);
            }
            catch (UnauthorizedAccessException)
            {
                throw;
            }
            catch (InvalidOperationException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading account from {Provider} for email {Email}", storageProvider.Name, email);
                throw new InvalidOperationException($"Error loading account from {storageProvider.Name}.");
            }
        }

        private string BuildFileName()
        {
            var aesid = AesEncryptionHelper.MakeAesId(_aes.CurrentValue);
            return _config.CurrentValue.StorageFileName.Replace("%AESID%", aesid);
        }

        private byte[] AesKey() => Convert.FromBase64String(_aes.CurrentValue.Key);
        private byte[] AesIv() => Convert.FromBase64String(_aes.CurrentValue.IV);
    }
}
