using System.Security.Cryptography;
using System.Text;
using Algorand.Algod.Model;
using BiatecSelfCustodyCore.Helper;
using BiatecSelfCustodyCore.Model;
using BiatecSelfCustodyCore.Providers;
using Microsoft.Extensions.Hosting;
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
            IHostEnvironment environment,
            ILogger<CloudAccountRepository> logger)
        {
            _catalog = catalog;
            _config = config;
            _aes = aes;
            _logger = logger;

            // Fail fast if AesOptions:ActiveKeyId doesn't resolve to a valid key - same precedent as
            // JwtIssuerService.LoadOrCreateSigningKey/ProviderAccessTokenProtector's constructor for other
            // load-bearing secrets in this repo. A misconfigured active key means every self-custody
            // account load/create would fail anyway, so surface it immediately rather than per-request.
            if (!environment.IsDevelopment())
            {
                AesKeyRingResolver.GetActiveKey(aes.CurrentValue, "AesOptions");
            }
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

                var activeKey = AesKeyRingResolver.GetActiveKey(_aes.CurrentValue, "AesOptions");
                var historicalKeys = AesKeyRingResolver.GetHistoricalKeys(_aes.CurrentValue, _logger);
                var fileNameTemplate = _config.CurrentValue.StorageFileName;

                byte[]? existing;
                try
                {
                    existing = await EncryptedKeyRingFileStore.LoadAsync(storageProvider, token, fileNameTemplate, activeKey, historicalKeys, email, _logger);
                }
                catch (CryptographicException cryptoEx)
                {
                    // Log full detail server-side only - the caller-facing message must not leak email,
                    // file size, or raw cryptographic exception text (an information-disclosure /
                    // padding-oracle amplifier).
                    _logger.LogError(cryptoEx, "Decryption failed for email {Email}.", email);
                    throw new InvalidOperationException("Unable to load the account. Please try re-pairing the device.");
                }
                catch (Exception decryptEx)
                {
                    _logger.LogError(decryptEx, "Failed to decrypt account data for email {Email}.", email);
                    throw new InvalidOperationException("Unable to load the account. Please try re-pairing the device.");
                }

                byte[] mnemonicBytes;
                if (existing == null)
                {
                    var newAccount = new Account();
                    mnemonicBytes = Encoding.UTF8.GetBytes(newAccount.ToMnemonic());
                    await EncryptedKeyRingFileStore.SaveAsync(storageProvider, token, fileNameTemplate, activeKey, email, mnemonicBytes);
                }
                else
                {
                    mnemonicBytes = existing;
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
    }
}
