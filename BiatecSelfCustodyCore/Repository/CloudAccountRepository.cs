using Algorand.Algod.Model;
using BiatecSelfCustodyCore.BusinessLogic;
using BiatecSelfCustodyCore.Helper;
using BiatecSelfCustodyCore.Model;
using Google.Apis.Auth.AspNetCore3;
using Google.Apis.Auth.OAuth2;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Security.Cryptography;
using System.Text;

namespace BiatecSelfCustodyCore.Repository
{
    /// <summary>
    /// Owns the AES encrypt/decrypt + ARC76 account-derivation logic for the self-custody account
    /// file, once, regardless of which cloud backend it's stored in - <see cref="GoogleDriveFileStore"/>
    /// and <see cref="OneDriveFileStore"/> are just dumb byte transports (Google Drive vs OneDrive).
    /// </summary>
    public class CloudAccountRepository : ICloudAccountRepository
    {
        private readonly IGoogleAuthProvider _googleAuth;
        private readonly IMicrosoftAuthProvider _microsoftAuth;
        private readonly GoogleDriveFileStore _googleStore;
        private readonly OneDriveFileStore _oneDriveStore;
        private readonly IOptionsMonitor<Configuration> _config;
        private readonly IOptionsMonitor<AesOptions> _aes;
        private readonly ILogger<CloudAccountRepository> _logger;

        public CloudAccountRepository(
            IGoogleAuthProvider googleAuth,
            IMicrosoftAuthProvider microsoftAuth,
            GoogleDriveFileStore googleStore,
            OneDriveFileStore oneDriveStore,
            IOptionsMonitor<Configuration> config,
            IOptionsMonitor<AesOptions> aes,
            ILogger<CloudAccountRepository> logger)
        {
            _googleAuth = googleAuth;
            _microsoftAuth = microsoftAuth;
            _googleStore = googleStore;
            _oneDriveStore = oneDriveStore;
            _config = config;
            _aes = aes;
            _logger = logger;
        }

        public async Task<Account> LoadAccountAsync(string email, int slot, StorageProvider provider, string? accessToken = null)
        {
            try
            {
                var fileName = BuildFileName();

                byte[]? existing;
                Func<byte[], Task> upload;

                if (provider == StorageProvider.Microsoft)
                {
                    var token = accessToken ?? await _microsoftAuth.GetAccessTokenAsync();
                    if (string.IsNullOrEmpty(token))
                    {
                        throw new UnauthorizedAccessException("No Microsoft access token available. Please sign in again.");
                    }

                    existing = await _oneDriveStore.TryDownloadAsync(fileName, token);
                    upload = content => _oneDriveStore.UploadAsync(fileName, content, token);
                }
                else
                {
                    var credential = accessToken != null
                        ? GoogleCredential.FromAccessToken(accessToken)
                        : await _googleAuth.GetCredentialAsync();
                    if (credential == null)
                    {
                        throw new UnauthorizedAccessException("No Google access token available. Please sign in again.");
                    }

                    existing = await _googleStore.TryDownloadAsync(fileName, credential);
                    upload = content => _googleStore.UploadAsync(fileName, content, credential);
                }

                byte[] mnemonicBytes;
                if (existing == null)
                {
                    var newAccount = new Account();
                    mnemonicBytes = Encoding.UTF8.GetBytes(newAccount.ToMnemonic());
                    var encrypted = AesEncryptionHelper.Encrypt(mnemonicBytes, AesKey(), AesIv(), email);
                    await upload(encrypted);
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
                _logger.LogError(ex, "Error loading account from {Provider} for email {Email}", provider, email);
                throw new InvalidOperationException($"Error loading account from {provider}.");
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
