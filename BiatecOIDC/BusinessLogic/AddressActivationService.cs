using System.Text.Json;
using BiatecSelfCustodyCore.Helper;
using BiatecSelfCustodyCore.Model;
using BiatecSelfCustodyCore.Providers;
using BiatecSelfCustodyCore.Repository;
using Microsoft.Extensions.Options;

namespace BiatecOIDC.BusinessLogic
{
    /// <inheritdoc cref="IAddressActivationService"/>
    /// <remarks>
    /// Stored under its own file name (<c>AddressActivations.%AESID%.dat</c>) - deliberately separate from
    /// the seed vault file (<c>CloudAccountRepository</c>) and the spending-limit files
    /// (<c>SpendingLimitService</c>), even though all three share the same AES key ring
    /// (<see cref="AesOptions"/>) and <see cref="EncryptedKeyRingFileStore"/> mechanics - a corrupted or
    /// unreadable activation file must never risk the account file itself, and the two are conceptually
    /// unrelated (one is key material, the other is just bookkeeping of already-derived/verified addresses).
    /// </remarks>
    public sealed class AddressActivationService : IAddressActivationService
    {
        private const string FileNameTemplate = "AddressActivations.%AESID%.dat";

        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

        private readonly ICloudStorageProviderCatalog _catalog;
        private readonly IOptionsMonitor<AesOptions> _aes;
        private readonly ILogger<AddressActivationService> _logger;

        public AddressActivationService(
            ICloudStorageProviderCatalog catalog,
            IOptionsMonitor<AesOptions> aes,
            IHostEnvironment environment,
            ILogger<AddressActivationService> logger)
        {
            _catalog = catalog;
            _aes = aes;
            _logger = logger;

            // Fail fast if AesOptions:ActiveKeyId doesn't resolve to a valid key - same precedent as
            // CloudAccountRepository/SpendingLimitService for other load-bearing secrets in this repo.
            if (!environment.IsDevelopment())
            {
                AesKeyRingResolver.GetActiveKey(aes.CurrentValue, "AesOptions");
            }
        }

        public async Task<AddressActivationEntry?> TryResolveAsync(string email, string provider, string? accessToken, string address, CancellationToken cancellationToken = default)
        {
            RequireEmail(email);
            var storageProvider = _catalog.Resolve(provider);
            var token = await ResolveAccessTokenAsync(storageProvider, accessToken);
            var activeKey = AesKeyRingResolver.GetActiveKey(_aes.CurrentValue, "AesOptions");
            var historicalKeys = AesKeyRingResolver.GetHistoricalKeys(_aes.CurrentValue, _logger);

            var document = await LoadDocumentAsync(storageProvider, token, activeKey, historicalKeys, email);
            return document.Entries.FirstOrDefault(e => string.Equals(e.Address, address, StringComparison.Ordinal));
        }

        public async Task<AddressActivationEntry> ActivateAsync(string email, string provider, string? accessToken, string address, string family, string seedAddress, int slot, CancellationToken cancellationToken = default)
        {
            var results = await ActivateManyAsync(email, provider, accessToken, new[] { (address, family, seedAddress, slot) }, cancellationToken);
            return results[0];
        }

        public async Task<IReadOnlyList<AddressActivationEntry>> ActivateManyAsync(string email, string provider, string? accessToken, IReadOnlyList<(string Address, string Family, string SeedAddress, int Slot)> activations, CancellationToken cancellationToken = default)
        {
            RequireEmail(email);
            if (activations.Count == 0)
            {
                return Array.Empty<AddressActivationEntry>();
            }

            foreach (var activation in activations)
            {
                if (string.IsNullOrWhiteSpace(activation.Address))
                {
                    throw new ArgumentException("Address is required.", nameof(activations));
                }
            }

            var storageProvider = _catalog.Resolve(provider);
            var token = await ResolveAccessTokenAsync(storageProvider, accessToken);
            var activeKey = AesKeyRingResolver.GetActiveKey(_aes.CurrentValue, "AesOptions");
            var historicalKeys = AesKeyRingResolver.GetHistoricalKeys(_aes.CurrentValue, _logger);

            var document = await LoadDocumentAsync(storageProvider, token, activeKey, historicalKeys, email);
            var baselineRawBytes = await DownloadActiveRawBytesAsync(storageProvider, token, activeKey);

            var now = DateTimeOffset.UtcNow;
            var results = new List<AddressActivationEntry>(activations.Count);
            foreach (var (address, family, seedAddress, slot) in activations)
            {
                var entry = new AddressActivationEntry
                {
                    Address = address,
                    Family = family,
                    SeedAddress = seedAddress,
                    Slot = slot,
                    ActivatedUtc = now
                };

                document.Entries.RemoveAll(e => string.Equals(e.Address, address, StringComparison.Ordinal));
                document.Entries.Add(entry);
                results.Add(entry);
            }

            await SaveDocumentWithConcurrencyCheckAsync(storageProvider, token, activeKey, email, document, baselineRawBytes);

            foreach (var (address, family, seedAddress, slot) in activations)
            {
                _logger.LogInformation("Activated address {Address} ({Family}) for {Email}, backed by seed {SeedAddress} slot {Slot}.", address, family, email, seedAddress, slot);
            }

            return results;
        }

        public async Task<IReadOnlyList<AddressActivationEntry>> ListAsync(string email, string provider, string? accessToken, CancellationToken cancellationToken = default)
        {
            RequireEmail(email);
            var storageProvider = _catalog.Resolve(provider);
            var token = await ResolveAccessTokenAsync(storageProvider, accessToken);
            var activeKey = AesKeyRingResolver.GetActiveKey(_aes.CurrentValue, "AesOptions");
            var historicalKeys = AesKeyRingResolver.GetHistoricalKeys(_aes.CurrentValue, _logger);

            var document = await LoadDocumentAsync(storageProvider, token, activeKey, historicalKeys, email);
            return document.Entries;
        }

        private async Task<AddressActivationDocument> LoadDocumentAsync(
            ICloudStorageProvider storageProvider,
            string token,
            AesKeyRingEntry activeKey,
            IReadOnlyList<AesKeyRingEntry> historicalKeys,
            string email)
        {
            byte[]? plaintext;
            try
            {
                plaintext = await EncryptedKeyRingFileStore.LoadAsync(storageProvider, token, FileNameTemplate, activeKey, historicalKeys, email, _logger);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unable to decrypt or parse {FileName} for {Email}.", FileNameTemplate, email);
                throw new InvalidOperationException("Unable to load address activation data. Please try again.");
            }

            if (plaintext == null)
            {
                return new AddressActivationDocument();
            }

            return JsonSerializer.Deserialize<AddressActivationDocument>(plaintext, JsonOptions) ?? new AddressActivationDocument();
        }

        private static async Task SaveDocumentAsync(ICloudStorageProvider storageProvider, string token, AesKeyRingEntry activeKey, string email, AddressActivationDocument document)
        {
            var plaintext = JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions);
            await EncryptedKeyRingFileStore.SaveAsync(storageProvider, token, FileNameTemplate, activeKey, email, plaintext);
        }

        /// <summary>The raw (still-encrypted) bytes currently at the active generation's file name, or <c>null</c> if no file exists there yet.</summary>
        private static Task<byte[]?> DownloadActiveRawBytesAsync(ICloudStorageProvider storageProvider, string token, AesKeyRingEntry activeKey) =>
            storageProvider.TryDownloadAsync(BuildActiveFileName(activeKey), token);

        private static string BuildActiveFileName(AesKeyRingEntry activeKey) =>
            FileNameTemplate.Replace("%AESID%", AesEncryptionHelper.MakeAesId(AesKeyRingResolver.KeyBytes(activeKey), AesKeyRingResolver.IvBytes(activeKey)));

        /// <summary>
        /// Saves <paramref name="document"/> only if the active file's raw bytes still match
        /// <paramref name="baselineRawBytes"/> - the exact same best-effort check-then-act re-verification
        /// <c>CloudAccountRepository.SaveVaultWithConcurrencyCheckAsync</c> uses for the seed vault, applied
        /// here so this file gets the same protection (audit finding M-04/R-029 - this file previously had
        /// none, unlike the seed vault since R-021's fix). Throws
        /// <see cref="VaultConcurrencyConflictException"/> rather than silently overwriting a concurrent
        /// writer's change if the check fails.
        /// </summary>
        private static async Task SaveDocumentWithConcurrencyCheckAsync(
            ICloudStorageProvider storageProvider,
            string token,
            AesKeyRingEntry activeKey,
            string email,
            AddressActivationDocument document,
            byte[]? baselineRawBytes)
        {
            var currentRawBytes = await DownloadActiveRawBytesAsync(storageProvider, token, activeKey);
            if (!RawBytesEqual(baselineRawBytes, currentRawBytes))
            {
                throw new VaultConcurrencyConflictException(
                    "The account's address activation data was modified by another request while this one was in progress. Please retry.");
            }

            await SaveDocumentAsync(storageProvider, token, activeKey, email, document);
        }

        private static bool RawBytesEqual(byte[]? a, byte[]? b)
        {
            if (a is null || b is null)
            {
                return a is null && b is null;
            }

            return a.AsSpan().SequenceEqual(b);
        }

        private static async Task<string> ResolveAccessTokenAsync(ICloudStorageProvider storageProvider, string? accessToken)
        {
            var token = accessToken ?? await storageProvider.GetAmbientAccessTokenAsync();
            if (string.IsNullOrEmpty(token))
            {
                throw new UnauthorizedAccessException($"No {storageProvider.Name} access token available. Please sign in again.");
            }

            return token;
        }

        private static void RequireEmail(string email)
        {
            if (string.IsNullOrWhiteSpace(email))
            {
                throw new ArgumentException("Email is required.", nameof(email));
            }
        }
    }
}
