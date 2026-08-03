using System.Text.Json;
using BiatecSelfCustodyCore.Helper;
using BiatecSelfCustodyCore.Model;
using BiatecSelfCustodyCore.Providers;
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
            var document = await LoadDocumentAsync(email, provider, accessToken);
            return document.Entries.FirstOrDefault(e => string.Equals(e.Address, address, StringComparison.Ordinal));
        }

        public async Task<AddressActivationEntry> ActivateAsync(string email, string provider, string? accessToken, string address, string family, string seedAddress, int slot, CancellationToken cancellationToken = default)
        {
            RequireEmail(email);
            if (string.IsNullOrWhiteSpace(address))
            {
                throw new ArgumentException("Address is required.", nameof(address));
            }

            var document = await LoadDocumentAsync(email, provider, accessToken);
            var entry = new AddressActivationEntry
            {
                Address = address,
                Family = family,
                SeedAddress = seedAddress,
                Slot = slot,
                ActivatedUtc = DateTimeOffset.UtcNow
            };

            document.Entries.RemoveAll(e => string.Equals(e.Address, address, StringComparison.Ordinal));
            document.Entries.Add(entry);

            await SaveDocumentAsync(email, provider, accessToken, document);
            _logger.LogInformation("Activated address {Address} ({Family}) for {Email}, backed by seed {SeedAddress} slot {Slot}.", address, family, email, seedAddress, slot);
            return entry;
        }

        public async Task<IReadOnlyList<AddressActivationEntry>> ListAsync(string email, string provider, string? accessToken, CancellationToken cancellationToken = default)
        {
            RequireEmail(email);
            var document = await LoadDocumentAsync(email, provider, accessToken);
            return document.Entries;
        }

        private async Task<AddressActivationDocument> LoadDocumentAsync(string email, string provider, string? accessToken)
        {
            var storageProvider = _catalog.Resolve(provider);
            var token = await ResolveAccessTokenAsync(storageProvider, accessToken);

            var activeKey = AesKeyRingResolver.GetActiveKey(_aes.CurrentValue, "AesOptions");
            var historicalKeys = AesKeyRingResolver.GetHistoricalKeys(_aes.CurrentValue, _logger);

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

        private async Task SaveDocumentAsync(string email, string provider, string? accessToken, AddressActivationDocument document)
        {
            var storageProvider = _catalog.Resolve(provider);
            var token = await ResolveAccessTokenAsync(storageProvider, accessToken);
            var activeKey = AesKeyRingResolver.GetActiveKey(_aes.CurrentValue, "AesOptions");

            var plaintext = JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions);
            await EncryptedKeyRingFileStore.SaveAsync(storageProvider, token, FileNameTemplate, activeKey, email, plaintext);
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
