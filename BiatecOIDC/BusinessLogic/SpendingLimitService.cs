using System.Text.Json;
using BiatecSelfCustodyCore.Helper;
using BiatecSelfCustodyCore.Model;
using BiatecSelfCustodyCore.Providers;
using Microsoft.Extensions.Options;

namespace BiatecOIDC.BusinessLogic
{
    /// <inheritdoc cref="ISpendingLimitService"/>
    public sealed class SpendingLimitService : ISpendingLimitService
    {
        // %AESID% is replaced the same way CloudAccountRepository does for the account file itself - a
        // short hash of the AES key generation that encrypted it (see EncryptedKeyRingFileStore), which is
        // what lets a rotated key still find and migrate an existing file instead of orphaning it.
        private const string LimitsFileNameTemplate = "SpendingLimits.%AESID%.dat";
        private const string LedgerFileNameTemplate = "SpendingLedger.%AESID%.dat";

        private static readonly TimeSpan DailyWindow = TimeSpan.FromHours(24);
        private static readonly TimeSpan WeeklyWindow = TimeSpan.FromDays(7);
        private static readonly TimeSpan MonthlyWindow = TimeSpan.FromDays(30);
        private static readonly TimeSpan MaxWindow = MonthlyWindow;

        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

        private readonly ICloudStorageProviderCatalog _catalog;
        private readonly IExchangeRateService _exchangeRateService;
        private readonly IOptionsMonitor<AesOptions> _aes;
        private readonly ILogger<SpendingLimitService> _logger;

        public SpendingLimitService(
            ICloudStorageProviderCatalog catalog,
            IExchangeRateService exchangeRateService,
            IOptionsMonitor<AesOptions> aes,
            IHostEnvironment environment,
            ILogger<SpendingLimitService> logger)
        {
            _catalog = catalog;
            _exchangeRateService = exchangeRateService;
            _aes = aes;
            _logger = logger;

            // Fail fast if AesOptions:ActiveKeyId doesn't resolve to a valid key - same precedent as
            // CloudAccountRepository/JwtIssuerService/ProviderAccessTokenProtector for other load-bearing
            // secrets in this repo.
            if (!environment.IsDevelopment())
            {
                AesKeyRingResolver.GetActiveKey(aes.CurrentValue, "AesOptions");
            }
        }

        public async Task<SpendingLimitSettings> GetLimitsAsync(string email, string provider, string? accessToken, string? seedAddress = null, int slot = 0, CancellationToken cancellationToken = default)
        {
            RequireEmail(email);
            var document = await LoadDocumentAsync(email, provider, accessToken);
            return ResolveBucket(document, seedAddress, slot);
        }

        public async Task SetLimitsAsync(string email, string provider, string? accessToken, SpendingLimitSettings settings, string? seedAddress = null, int slot = 0, CancellationToken cancellationToken = default)
        {
            RequireEmail(email);
            ArgumentNullException.ThrowIfNull(settings);

            if (!await _exchangeRateService.IsSupportedCurrencyAsync(settings.CurrencyCode, cancellationToken))
            {
                throw new UnsupportedCurrencyException(settings.CurrencyCode);
            }

            var normalized = new SpendingLimitSettings
            {
                CurrencyCode = settings.CurrencyCode.Trim().ToUpperInvariant(),
                DailyLimit = settings.DailyLimit,
                WeeklyLimit = settings.WeeklyLimit,
                MonthlyLimit = settings.MonthlyLimit
            };

            var document = await LoadDocumentAsync(email, provider, accessToken);
            if (seedAddress == null)
            {
                document.Global = normalized;
            }
            else
            {
                document.PerAddress[BuildAddressKey(seedAddress, slot)] = normalized;
            }

            await SaveDocumentAsync(email, provider, accessToken, document);
        }

        public async Task EnsureWithinLimitsAsync(string email, string provider, string? accessToken, decimal amountUsd, string seedAddress, int slot, CancellationToken cancellationToken = default)
        {
            RequireEmail(email);
            if (string.IsNullOrWhiteSpace(seedAddress))
            {
                throw new ArgumentException("A resolved signing address is required.", nameof(seedAddress));
            }

            var document = await LoadDocumentAsync(email, provider, accessToken);
            var global = document.Global;
            document.PerAddress.TryGetValue(BuildAddressKey(seedAddress, slot), out var address);

            var globalUnbounded = IsUnbounded(global);
            var addressUnbounded = address == null || IsUnbounded(address);
            if (globalUnbounded && addressUnbounded)
            {
                return; // Neither tier configured - no need to even read the ledger.
            }

            var ledger = await LoadLedgerAsync(email, provider, accessToken);
            var now = DateTimeOffset.UtcNow;

            if (!globalUnbounded)
            {
                var newAmountInCurrency = await _exchangeRateService.ConvertFromUsdAsync(amountUsd, global.CurrencyCode, cancellationToken);
                await CheckWindowAsync("global-daily", DailyWindow, global.DailyLimit, ledger, now, newAmountInCurrency, global.CurrencyCode, cancellationToken);
                await CheckWindowAsync("global-weekly", WeeklyWindow, global.WeeklyLimit, ledger, now, newAmountInCurrency, global.CurrencyCode, cancellationToken);
                await CheckWindowAsync("global-monthly", MonthlyWindow, global.MonthlyLimit, ledger, now, newAmountInCurrency, global.CurrencyCode, cancellationToken);
            }

            if (!addressUnbounded)
            {
                var addressLedger = ledger.Where(e =>
                    string.Equals(e.SeedAddress, seedAddress, StringComparison.Ordinal) && e.Slot == slot).ToList();
                var newAmountInCurrency = await _exchangeRateService.ConvertFromUsdAsync(amountUsd, address!.CurrencyCode, cancellationToken);
                await CheckWindowAsync("address-daily", DailyWindow, address.DailyLimit, addressLedger, now, newAmountInCurrency, address.CurrencyCode, cancellationToken);
                await CheckWindowAsync("address-weekly", WeeklyWindow, address.WeeklyLimit, addressLedger, now, newAmountInCurrency, address.CurrencyCode, cancellationToken);
                await CheckWindowAsync("address-monthly", MonthlyWindow, address.MonthlyLimit, addressLedger, now, newAmountInCurrency, address.CurrencyCode, cancellationToken);
            }
        }

        private static bool IsUnbounded(SpendingLimitSettings settings) =>
            settings.DailyLimit <= 0 && settings.WeeklyLimit <= 0 && settings.MonthlyLimit <= 0;

        internal static string BuildAddressKey(string seedAddress, int slot) => $"{seedAddress}:{slot}";

        private static SpendingLimitSettings ResolveBucket(SpendingLimitsDocument document, string? seedAddress, int slot)
        {
            if (seedAddress == null)
            {
                return document.Global;
            }

            return document.PerAddress.TryGetValue(BuildAddressKey(seedAddress, slot), out var settings) ? settings : new SpendingLimitSettings();
        }

        /// <summary>
        /// Loads the settings document, migrating a legacy flat <see cref="SpendingLimitSettings"/> file
        /// (predating the global/per-address split) into <c>{ Global = &lt;that&gt;, PerAddress = {} }</c>
        /// and re-saving it immediately - same "migrate on read, re-save immediately" precedent as
        /// <c>CloudAccountRepository</c>'s legacy-mnemonic migration.
        /// </summary>
        private async Task<SpendingLimitsDocument> LoadDocumentAsync(string email, string provider, string? accessToken)
        {
            var storageProvider = _catalog.Resolve(provider);
            var token = await ResolveAccessTokenAsync(storageProvider, accessToken);

            var activeKey = AesKeyRingResolver.GetActiveKey(_aes.CurrentValue, "AesOptions");
            var historicalKeys = AesKeyRingResolver.GetHistoricalKeys(_aes.CurrentValue, _logger);

            byte[]? plaintext;
            try
            {
                plaintext = await EncryptedKeyRingFileStore.LoadAsync(storageProvider, token, LimitsFileNameTemplate, activeKey, historicalKeys, email, _logger);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unable to decrypt or parse {FileName} for {Email}.", LimitsFileNameTemplate, email);
                throw new InvalidOperationException("Unable to load spending-limit data. Please try again.");
            }

            if (plaintext == null)
            {
                return new SpendingLimitsDocument();
            }

            var (document, wasLegacyShape) = ParseDocument(plaintext);
            if (wasLegacyShape)
            {
                await SaveDocumentAsync(email, provider, accessToken, document);
            }

            return document;
        }

        private static (SpendingLimitsDocument Document, bool WasLegacyShape) ParseDocument(byte[] plaintext)
        {
            using var probe = System.Text.Json.JsonDocument.Parse(plaintext);
            if (probe.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object &&
                probe.RootElement.TryGetProperty("global", out _))
            {
                var document = JsonSerializer.Deserialize<SpendingLimitsDocument>(plaintext, JsonOptions) ?? new SpendingLimitsDocument();
                return (document, false);
            }

            // Legacy shape: the whole file was just a flat SpendingLimitSettings object.
            var legacy = JsonSerializer.Deserialize<SpendingLimitSettings>(plaintext, JsonOptions) ?? new SpendingLimitSettings();
            return (new SpendingLimitsDocument { Global = legacy }, true);
        }

        private async Task SaveDocumentAsync(string email, string provider, string? accessToken, SpendingLimitsDocument document)
        {
            var storageProvider = _catalog.Resolve(provider);
            var token = await ResolveAccessTokenAsync(storageProvider, accessToken);
            var activeKey = AesKeyRingResolver.GetActiveKey(_aes.CurrentValue, "AesOptions");

            var plaintext = JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions);
            await EncryptedKeyRingFileStore.SaveAsync(storageProvider, token, LimitsFileNameTemplate, activeKey, email, plaintext);
        }

        public async Task RecordSpendAsync(string email, string provider, string? accessToken, IReadOnlyList<SpendingLedgerEntry> entries, CancellationToken cancellationToken = default)
        {
            RequireEmail(email);
            if (entries.Count == 0)
            {
                return;
            }

            var ledger = await LoadLedgerAsync(email, provider, accessToken);
            var cutoff = DateTimeOffset.UtcNow - MaxWindow;

            var combined = new List<SpendingLedgerEntry>(ledger.Count + entries.Count);
            combined.AddRange(ledger);
            combined.AddRange(entries);
            var pruned = combined.Where(e => e.TimestampUtc >= cutoff).ToList();

            await SaveEncryptedJsonAsync(email, provider, accessToken, LedgerFileNameTemplate, pruned);
        }

        private async Task CheckWindowAsync(
            string windowName,
            TimeSpan window,
            decimal limit,
            IReadOnlyList<SpendingLedgerEntry> ledger,
            DateTimeOffset now,
            decimal newAmountInCurrency,
            string currencyCode,
            CancellationToken cancellationToken)
        {
            if (limit <= 0)
            {
                return;
            }

            var windowStart = now - window;
            var existingUsd = ledger.Where(e => e.TimestampUtc >= windowStart).Sum(e => e.AmountUsd);
            var existingInCurrency = await _exchangeRateService.ConvertFromUsdAsync(existingUsd, currencyCode, cancellationToken);
            var projected = existingInCurrency + newAmountInCurrency;

            if (projected > limit)
            {
                throw new SpendingLimitExceededException(windowName, projected, limit, currencyCode);
            }
        }

        private async Task<List<SpendingLedgerEntry>> LoadLedgerAsync(string email, string provider, string? accessToken)
        {
            var ledger = await LoadEncryptedJsonAsync<List<SpendingLedgerEntry>>(email, provider, accessToken, LedgerFileNameTemplate);
            return ledger ?? new List<SpendingLedgerEntry>();
        }

        private async Task<T?> LoadEncryptedJsonAsync<T>(string email, string provider, string? accessToken, string fileNameTemplate) where T : class
        {
            var storageProvider = _catalog.Resolve(provider);
            var token = await ResolveAccessTokenAsync(storageProvider, accessToken);

            var activeKey = AesKeyRingResolver.GetActiveKey(_aes.CurrentValue, "AesOptions");
            var historicalKeys = AesKeyRingResolver.GetHistoricalKeys(_aes.CurrentValue, _logger);

            try
            {
                var plaintext = await EncryptedKeyRingFileStore.LoadAsync(storageProvider, token, fileNameTemplate, activeKey, historicalKeys, email, _logger);
                return plaintext == null ? null : JsonSerializer.Deserialize<T>(plaintext, JsonOptions);
            }
            catch (Exception ex)
            {
                // Same information-disclosure concern as CloudAccountRepository: log detail server-side
                // only, never leak email/file size/raw crypto exception text to the caller.
                _logger.LogError(ex, "Unable to decrypt or parse {FileName} for {Email}.", fileNameTemplate, email);
                throw new InvalidOperationException("Unable to load spending-limit data. Please try again.");
            }
        }

        private async Task SaveEncryptedJsonAsync<T>(string email, string provider, string? accessToken, string fileNameTemplate, T value)
        {
            var storageProvider = _catalog.Resolve(provider);
            var token = await ResolveAccessTokenAsync(storageProvider, accessToken);
            var activeKey = AesKeyRingResolver.GetActiveKey(_aes.CurrentValue, "AesOptions");

            var plaintext = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
            await EncryptedKeyRingFileStore.SaveAsync(storageProvider, token, fileNameTemplate, activeKey, email, plaintext);
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
