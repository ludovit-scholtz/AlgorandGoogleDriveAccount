using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
    /// Owns the AES encrypt/decrypt + ARC76 account-derivation logic for the self-custody seed vault, once,
    /// regardless of which cloud provider it's stored with - resolves the provider from
    /// <see cref="ICloudStorageProviderCatalog"/> and only ever talks to it through
    /// <see cref="ICloudStorageProvider"/>, so adding a new provider never requires touching this class.
    /// </summary>
    public class CloudAccountRepository : ICloudAccountRepository
    {
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

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
            // Also fails fast if the resolved active key is the known placeholder value committed in
            // k8s/main/conf-*/appsettings.json (security audit findings R-019/G-02) - closing the gap where
            // a missing secret override would previously start up successfully and silently serve
            // production traffic under a publicly-committed key.
            if (!environment.IsDevelopment())
            {
                var activeKey = AesKeyRingResolver.GetActiveKey(aes.CurrentValue, "AesOptions");
                AesKeyRingResolver.EnsureActiveKeyIsNotKnownPlaceholder(activeKey, "AesOptions");
            }
        }

        public async Task<Account> LoadAccountAsync(string email, int slot, string provider, string? accessToken = null, string? primaryAddress = null)
        {
            var storageProvider = _catalog.Resolve(provider);

            try
            {
                var context = await ResolveContextAsync(storageProvider, accessToken);
                var seed = await ResolveSeedEntryAsync(email, storageProvider, context, primaryAddress);

                return AlgorandARC76AccountDotNet.ARC76.GetEmailAccount(email, seed.Mnemonic, slot);
            }
            catch (UnauthorizedAccessException)
            {
                throw;
            }
            catch (InvalidOperationException)
            {
                throw;
            }
            catch (VaultConcurrencyConflictException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading account from {Provider} for email {Email}", storageProvider.Name, email);
                throw new InvalidOperationException($"Error loading account from {storageProvider.Name}.");
            }
        }

        public async Task<string> DeriveAddressAsync(string email, string provider, string? primaryAddress, int slot, string? accessToken = null)
        {
            var storageProvider = _catalog.Resolve(provider);

            try
            {
                var context = await ResolveContextAsync(storageProvider, accessToken);
                var seed = await ResolveSeedEntryAsync(email, storageProvider, context, primaryAddress);

                return AlgorandARC76AccountDotNet.ARC76.GetEmailAccount(email, seed.Mnemonic, slot).Address.EncodeAsString();
            }
            catch (UnauthorizedAccessException)
            {
                throw;
            }
            catch (InvalidOperationException)
            {
                throw;
            }
            catch (VaultConcurrencyConflictException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deriving an address from {Provider} for email {Email}", storageProvider.Name, email);
                throw new InvalidOperationException($"Error deriving an address from {storageProvider.Name}.");
            }
        }

        public async Task<string> ResolveSeedAddressAsync(string email, string provider, string? primaryAddress, string? accessToken = null)
        {
            var storageProvider = _catalog.Resolve(provider);

            try
            {
                var context = await ResolveContextAsync(storageProvider, accessToken);
                var seed = await ResolveSeedEntryAsync(email, storageProvider, context, primaryAddress);

                return seed.PrimaryAddress;
            }
            catch (UnauthorizedAccessException)
            {
                throw;
            }
            catch (InvalidOperationException)
            {
                throw;
            }
            catch (VaultConcurrencyConflictException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error resolving the seed address from {Provider} for email {Email}", storageProvider.Name, email);
                throw new InvalidOperationException($"Error resolving the seed address from {storageProvider.Name}.");
            }
        }

        /// <summary>
        /// Resolves <paramref name="primaryAddress"/> to a concrete <see cref="SeedVaultEntry"/> - <c>null</c>
        /// resolves to (and, if needed, auto-creates) the vault's current primary seed, exactly like the
        /// pre-multi-address behavior; a non-null value must already exist in the vault and is never
        /// auto-created, since the caller named a specific seed.
        /// </summary>
        private async Task<SeedVaultEntry> ResolveSeedEntryAsync(string email, ICloudStorageProvider storageProvider, VaultContext context, string? primaryAddress)
        {
            if (primaryAddress == null)
            {
                var vault = await LoadVaultEnsuringAtLeastOneSeedAsync(email, storageProvider, context);
                return ResolvePrimary(vault);
            }

            var lookupVault = await LoadVaultOrEmptyAsync(email, storageProvider, context);
            var seed = lookupVault.Seeds.FirstOrDefault(s => string.Equals(s.PrimaryAddress, primaryAddress, StringComparison.Ordinal));
            if (seed == null)
            {
                throw new InvalidOperationException($"No seed with address '{primaryAddress}' exists for this account.");
            }

            return seed;
        }

        public async Task<IReadOnlyList<SeedSummary>> ListSeedsAsync(string email, string provider, string? accessToken = null)
        {
            var storageProvider = _catalog.Resolve(provider);

            try
            {
                var context = await ResolveContextAsync(storageProvider, accessToken);
                // Read-only - unlike LoadAccountAsync, does not side-effect create a first seed just
                // because it was asked to list them; a user who's never had an account yet simply sees
                // an empty list.
                var vault = await LoadVaultOrEmptyAsync(email, storageProvider, context);

                return vault.Seeds
                    .OrderBy(s => s.CreatedUtc)
                    .Select(s => new SeedSummary(s.PrimaryAddress, s.CreatedUtc, s.IsPrimary))
                    .ToList();
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
                _logger.LogError(ex, "Error listing seeds from {Provider} for email {Email}", storageProvider.Name, email);
                throw new InvalidOperationException($"Error listing seeds from {storageProvider.Name}.");
            }
        }

        public async Task<SeedSummary> CreateSeedAsync(string email, string provider, string? accessToken = null)
        {
            var storageProvider = _catalog.Resolve(provider);

            try
            {
                var context = await ResolveContextAsync(storageProvider, accessToken);
                // Not LoadVaultEnsuringAtLeastOneSeedAsync - this method is itself the seed-creation path,
                // so it must see a genuinely empty vault as empty (to correctly make the very first seed
                // primary), not have one silently pre-created for it first.
                var vault = await LoadVaultOrEmptyAsync(email, storageProvider, context);
                var baselineRawBytes = await DownloadActiveRawBytesAsync(storageProvider, context);

                var newAccount = new Account();
                var entry = BuildSeedEntry(email, newAccount.ToMnemonic(), isPrimary: vault.Seeds.Count == 0);
                vault.Seeds.Add(entry);

                await SaveVaultWithConcurrencyCheckAsync(storageProvider, context, email, vault, baselineRawBytes);

                _logger.LogInformation("Generated a new seed ({Address}) for {Email}.", entry.PrimaryAddress, email);
                return new SeedSummary(entry.PrimaryAddress, entry.CreatedUtc, entry.IsPrimary);
            }
            catch (UnauthorizedAccessException)
            {
                throw;
            }
            catch (InvalidOperationException)
            {
                throw;
            }
            catch (VaultConcurrencyConflictException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating a new seed on {Provider} for email {Email}", storageProvider.Name, email);
                throw new InvalidOperationException($"Error creating a new seed on {storageProvider.Name}.");
            }
        }

        public async Task SwitchPrimarySeedAsync(string email, string provider, string primaryAddress, string? accessToken = null)
        {
            var storageProvider = _catalog.Resolve(provider);

            try
            {
                var context = await ResolveContextAsync(storageProvider, accessToken);
                var vault = await LoadVaultOrEmptyAsync(email, storageProvider, context);
                var baselineRawBytes = await DownloadActiveRawBytesAsync(storageProvider, context);

                var target = vault.Seeds.FirstOrDefault(s => string.Equals(s.PrimaryAddress, primaryAddress, StringComparison.Ordinal));
                if (target == null)
                {
                    throw new InvalidOperationException($"No seed with address '{primaryAddress}' exists for this account.");
                }

                foreach (var seed in vault.Seeds)
                {
                    seed.IsPrimary = ReferenceEquals(seed, target);
                }

                await SaveVaultWithConcurrencyCheckAsync(storageProvider, context, email, vault, baselineRawBytes);
                _logger.LogInformation("Switched the primary seed to {Address} for {Email}.", primaryAddress, email);
            }
            catch (UnauthorizedAccessException)
            {
                throw;
            }
            catch (InvalidOperationException)
            {
                throw;
            }
            catch (VaultConcurrencyConflictException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error switching the primary seed on {Provider} for email {Email}", storageProvider.Name, email);
                throw new InvalidOperationException($"Error switching the primary seed on {storageProvider.Name}.");
            }
        }

        public async Task<(string FileName, byte[] EncryptedBytes)> GetEncryptedVaultForBackupAsync(string email, string provider, string? accessToken = null)
        {
            var storageProvider = _catalog.Resolve(provider);

            try
            {
                var context = await ResolveContextAsync(storageProvider, accessToken);

                // Ensures the vault exists and, if it was only found under a historical key generation,
                // migrates it onto the active one first - the backup copy should always be the same
                // canonical file a normal LoadAccountAsync call would use.
                await LoadVaultEnsuringAtLeastOneSeedAsync(email, storageProvider, context);

                var activeFileName = BuildActiveFileName(context);
                var bytes = await storageProvider.TryDownloadAsync(activeFileName, context.Token);
                if (bytes == null)
                {
                    // Shouldn't happen - LoadOrCreateVaultAsync above just ensured this file exists.
                    throw new InvalidOperationException("Vault file unexpectedly missing after ensuring it exists.");
                }

                return (activeFileName, bytes);
            }
            catch (UnauthorizedAccessException)
            {
                throw;
            }
            catch (InvalidOperationException)
            {
                throw;
            }
            catch (VaultConcurrencyConflictException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error reading the vault from {Provider} for backup, for email {Email}", storageProvider.Name, email);
                throw new InvalidOperationException($"Error reading the vault from {storageProvider.Name} for backup.");
            }
        }

        private static string BuildActiveFileName(VaultContext context) =>
            context.FileNameTemplate.Replace("%AESID%", AesEncryptionHelper.MakeAesId(AesKeyRingResolver.KeyBytes(context.ActiveKey), AesKeyRingResolver.IvBytes(context.ActiveKey)));

        private sealed record VaultContext(string Token, AesKeyRingEntry ActiveKey, IReadOnlyList<AesKeyRingEntry> HistoricalKeys, string FileNameTemplate);

        private async Task<VaultContext> ResolveContextAsync(ICloudStorageProvider storageProvider, string? accessToken)
        {
            var token = accessToken ?? await storageProvider.GetAmbientAccessTokenAsync();
            if (string.IsNullOrEmpty(token))
            {
                throw new UnauthorizedAccessException($"No {storageProvider.Name} access token available. Please sign in again.");
            }

            var activeKey = AesKeyRingResolver.GetActiveKey(_aes.CurrentValue, "AesOptions");
            var historicalKeys = AesKeyRingResolver.GetHistoricalKeys(_aes.CurrentValue, _logger);
            var fileNameTemplate = _config.CurrentValue.StorageFileName;
            return new VaultContext(token, activeKey, historicalKeys, fileNameTemplate);
        }

        /// <summary>
        /// Loads the vault as-is - an empty (zero-seed) <see cref="SeedVault"/> if no file exists yet, never
        /// side-effect creating a first seed. Callers that need "at least one seed always exists" (e.g.
        /// <see cref="LoadAccountAsync"/>, which needs something to derive from) use
        /// <see cref="LoadVaultEnsuringAtLeastOneSeedAsync"/> instead; callers that are themselves the
        /// seed-creation path (<see cref="CreateSeedAsync"/>) need to see a genuinely empty vault as empty,
        /// to correctly decide whether the seed they're about to add is the very first one.
        /// </summary>
        private async Task<SeedVault> LoadVaultOrEmptyAsync(string email, ICloudStorageProvider storageProvider, VaultContext context)
        {
            byte[]? existing;
            try
            {
                existing = await EncryptedKeyRingFileStore.LoadAsync(storageProvider, context.Token, context.FileNameTemplate, context.ActiveKey, context.HistoricalKeys, email, _logger);
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

            if (existing == null)
            {
                return new SeedVault();
            }

            var parsed = TryParseVault(existing);
            if (parsed != null)
            {
                return parsed;
            }

            // Legacy pre-vault format: the whole file was just the raw UTF-8 mnemonic text. Migrate once,
            // in place, into a single-seed vault - never a data loss, and every subsequent read hits the
            // fast (already-JSON) path.
            var legacyMnemonic = Encoding.UTF8.GetString(existing);
            var migratedVault = new SeedVault();
            migratedVault.Seeds.Add(BuildSeedEntry(email, legacyMnemonic, isPrimary: true));
            await SaveVaultAsync(storageProvider, context, email, migratedVault);
            _logger.LogInformation("Migrated the legacy single-seed account file to the multi-seed vault format for {Email}.", email);
            return migratedVault;
        }

        /// <summary>Like <see cref="LoadVaultOrEmptyAsync"/>, but if the vault is empty (a user's very first access), creates and persists its first seed (primary) before returning.</summary>
        private async Task<SeedVault> LoadVaultEnsuringAtLeastOneSeedAsync(string email, ICloudStorageProvider storageProvider, VaultContext context)
        {
            var vault = await LoadVaultOrEmptyAsync(email, storageProvider, context);
            if (vault.Seeds.Count == 0)
            {
                // Only this branch writes, so only this branch needs the concurrency check - two
                // concurrent first-ever loads could otherwise both decide to create the first seed and
                // race each other, silently discarding one of the two generated seeds.
                var baselineRawBytes = await DownloadActiveRawBytesAsync(storageProvider, context);
                var newAccount = new Account();
                vault.Seeds.Add(BuildSeedEntry(email, newAccount.ToMnemonic(), isPrimary: true));
                await SaveVaultWithConcurrencyCheckAsync(storageProvider, context, email, vault, baselineRawBytes);
            }

            return vault;
        }

        private static SeedVault? TryParseVault(byte[] plaintext)
        {
            try
            {
                var vault = JsonSerializer.Deserialize<SeedVault>(plaintext, JsonOptions);
                return vault?.Seeds.Count > 0 ? vault : null;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static SeedVaultEntry BuildSeedEntry(string email, string mnemonic, bool isPrimary)
        {
            var address = AlgorandARC76AccountDotNet.ARC76.GetEmailAccount(email, mnemonic, slot: 0).Address.EncodeAsString();
            return new SeedVaultEntry
            {
                Mnemonic = mnemonic,
                PrimaryAddress = address,
                CreatedUtc = DateTimeOffset.UtcNow,
                IsPrimary = isPrimary
            };
        }

        private static SeedVaultEntry ResolvePrimary(SeedVault vault)
        {
            // Defensive fallback (first entry) in case an externally-edited vault ever has zero seeds
            // flagged primary - every mutation path in this class maintains the "exactly one primary"
            // invariant, so this only matters for data that wasn't written by this class.
            return vault.Seeds.FirstOrDefault(s => s.IsPrimary) ?? vault.Seeds[0];
        }

        private static async Task SaveVaultAsync(ICloudStorageProvider storageProvider, VaultContext context, string email, SeedVault vault)
        {
            var plaintext = JsonSerializer.SerializeToUtf8Bytes(vault, JsonOptions);
            await EncryptedKeyRingFileStore.SaveAsync(storageProvider, context.Token, context.FileNameTemplate, context.ActiveKey, email, plaintext);
        }

        /// <summary>The raw (still-encrypted) bytes currently at the active generation's file name, or <c>null</c> if no file exists there yet.</summary>
        private static Task<byte[]?> DownloadActiveRawBytesAsync(ICloudStorageProvider storageProvider, VaultContext context) =>
            storageProvider.TryDownloadAsync(BuildActiveFileName(context), context.Token);

        /// <summary>
        /// Saves <paramref name="vault"/> only if the active file's raw bytes still match
        /// <paramref name="baselineRawBytes"/> (captured by the caller right after its own read, including
        /// after any read-time migration write) - i.e. nothing else wrote to this account's vault between
        /// this call's read and this call's write. Narrows, but does not eliminate, the seed-vault race
        /// condition described in security audit finding M-01/R-021: neither Google Drive nor OneDrive's
        /// API is used here with a true atomic compare-and-swap (Graph's native <c>If-Match</c>/<c>eTag</c>
        /// support would allow one; Drive's revision model is more limited), so this is a best-effort
        /// check-then-act re-verification immediately before the write, not a provider-enforced guarantee -
        /// but it shrinks the window in which two concurrent mutations can silently race from "the entire
        /// request" down to "the gap between this re-check and the upload that immediately follows it".
        /// Throws <see cref="VaultConcurrencyConflictException"/> (never overwrites silently) if the check
        /// fails, so the caller can surface a retryable error instead of another request's change vanishing.
        /// </summary>
        private static async Task SaveVaultWithConcurrencyCheckAsync(
            ICloudStorageProvider storageProvider,
            VaultContext context,
            string email,
            SeedVault vault,
            byte[]? baselineRawBytes)
        {
            var currentRawBytes = await DownloadActiveRawBytesAsync(storageProvider, context);
            if (!RawBytesEqual(baselineRawBytes, currentRawBytes))
            {
                throw new VaultConcurrencyConflictException(
                    "The account's seed vault was modified by another request while this one was in progress. Please retry.");
            }

            await SaveVaultAsync(storageProvider, context, email, vault);
        }

        private static bool RawBytesEqual(byte[]? a, byte[]? b)
        {
            if (a is null || b is null)
            {
                return a is null && b is null;
            }

            return a.AsSpan().SequenceEqual(b);
        }
    }
}
