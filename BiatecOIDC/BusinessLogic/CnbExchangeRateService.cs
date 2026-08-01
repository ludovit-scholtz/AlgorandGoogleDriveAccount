using System.Text.Json;
using BiatecOIDC.Model;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;

namespace BiatecOIDC.BusinessLogic
{
    /// <inheritdoc cref="IExchangeRateService"/>
    /// <remarks>
    /// The ČNB feed publishes CZK-per-unit rates, so every conversion is computed via CZK as the pivot
    /// currency: <c>usdPerUnit(C) = czkPerUnit(C) / czkPerUnit(USD)</c>. The whole day's rate table is
    /// fetched once and cached as a single JSON blob in <see cref="IDistributedCache"/> (shared, not
    /// per-user - these are public market rates) for <see cref="ExchangeRateConfiguration.CacheDurationMinutes"/>.
    /// </remarks>
    public sealed class CnbExchangeRateService : IExchangeRateService
    {
        private const string CacheKey = "oidc:fx:cnb-daily-rates";
        internal const string UsdCode = "USD";
        internal const string CzkCode = "CZK";

        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

        private readonly HttpClient _httpClient;
        private readonly IDistributedCache _cache;
        private readonly IOptionsMonitor<ExchangeRateConfiguration> _config;
        private readonly ILogger<CnbExchangeRateService> _logger;

        public CnbExchangeRateService(
            HttpClient httpClient,
            IDistributedCache cache,
            IOptionsMonitor<ExchangeRateConfiguration> config,
            ILogger<CnbExchangeRateService> logger)
        {
            _httpClient = httpClient;
            _cache = cache;
            _config = config;
            _logger = logger;
        }

        public async Task<IReadOnlyList<CurrencyRate>> GetSupportedCurrenciesAsync(CancellationToken cancellationToken = default)
        {
            var rates = await GetCzkPerUnitRatesAsync(cancellationToken);
            var czkPerUsd = rates[UsdCode].CzkPerUnit;

            return rates.Values
                .Select(r => new CurrencyRate
                {
                    Code = r.CurrencyCode,
                    DisplayName = string.IsNullOrEmpty(r.DisplayName) ? null : r.DisplayName,
                    UsdPerUnit = r.CzkPerUnit / czkPerUsd
                })
                .OrderBy(r => r.Code, StringComparer.Ordinal)
                .ToList();
        }

        public async Task<decimal> ConvertFromUsdAsync(decimal amountUsd, string currencyCode, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(currencyCode))
            {
                throw new UnsupportedCurrencyException(currencyCode ?? string.Empty);
            }

            var normalized = currencyCode.Trim().ToUpperInvariant();
            if (normalized == UsdCode)
            {
                // Nothing to convert, and no reason to require the CNB feed to be reachable just to
                // confirm the caller's own default currency.
                return amountUsd;
            }

            var rates = await GetCzkPerUnitRatesAsync(cancellationToken);
            if (!rates.TryGetValue(normalized, out var target))
            {
                throw new UnsupportedCurrencyException(currencyCode);
            }

            var czkPerUsd = rates[UsdCode].CzkPerUnit;
            var usdPerUnit = target.CzkPerUnit / czkPerUsd;
            return amountUsd / usdPerUnit;
        }

        public async Task<bool> IsSupportedCurrencyAsync(string? currencyCode, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(currencyCode))
            {
                return false;
            }

            var normalized = currencyCode.Trim().ToUpperInvariant();
            if (normalized == UsdCode)
            {
                return true;
            }

            var rates = await GetCzkPerUnitRatesAsync(cancellationToken);
            return rates.ContainsKey(normalized);
        }

        private async Task<IReadOnlyDictionary<string, RateEntry>> GetCzkPerUnitRatesAsync(CancellationToken cancellationToken)
        {
            var cached = await _cache.GetStringAsync(CacheKey, cancellationToken);
            if (!string.IsNullOrEmpty(cached))
            {
                try
                {
                    var deserialized = JsonSerializer.Deserialize<Dictionary<string, RateEntry>>(cached, JsonOptions);
                    if (deserialized != null && deserialized.Count > 0)
                    {
                        return deserialized;
                    }
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning(ex, "Corrupt cached CNB exchange rate table; re-fetching.");
                }
            }

            return await FetchAndCacheRatesAsync(cancellationToken);
        }

        private async Task<IReadOnlyDictionary<string, RateEntry>> FetchAndCacheRatesAsync(CancellationToken cancellationToken)
        {
            var response = await _httpClient.GetFromJsonAsync<CnbDailyRatesResponse>(_config.CurrentValue.CnbDailyRatesUrl, JsonOptions, cancellationToken)
                ?? throw new InvalidOperationException("The Czech National Bank exchange rate API returned no data.");

            var byCode = new Dictionary<string, RateEntry>(StringComparer.Ordinal);
            foreach (var rate in response.Rates)
            {
                if (string.IsNullOrWhiteSpace(rate.CurrencyCode) || rate.Amount <= 0)
                {
                    continue;
                }

                var code = rate.CurrencyCode.Trim().ToUpperInvariant();
                byCode[code] = new RateEntry
                {
                    CurrencyCode = code,
                    DisplayName = FormatDisplayName(rate.Country, rate.Currency),
                    CzkPerUnit = rate.Rate / rate.Amount
                };
            }

            if (!byCode.ContainsKey(UsdCode))
            {
                throw new InvalidOperationException("The Czech National Bank exchange rate feed did not include a USD rate.");
            }

            // ČNB never quotes CZK against itself, but CZK is a valid limit currency too - 1:1 with CZK by definition.
            byCode[CzkCode] = new RateEntry { CurrencyCode = CzkCode, DisplayName = "Czech koruna", CzkPerUnit = 1m };

            var json = JsonSerializer.Serialize(byCode, JsonOptions);
            await _cache.SetStringAsync(CacheKey, json, new DistributedCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(Math.Max(1, _config.CurrentValue.CacheDurationMinutes))
            }, cancellationToken);

            return byCode;
        }

        private static string? FormatDisplayName(string? country, string? currency)
        {
            if (string.IsNullOrWhiteSpace(country) && string.IsNullOrWhiteSpace(currency))
            {
                return null;
            }

            if (string.IsNullOrWhiteSpace(country))
            {
                return currency;
            }

            if (string.IsNullOrWhiteSpace(currency))
            {
                return country;
            }

            return $"{country} {currency}";
        }

        private sealed class RateEntry
        {
            public string CurrencyCode { get; set; } = string.Empty;
            public string? DisplayName { get; set; }
            public decimal CzkPerUnit { get; set; }
        }
    }
}
