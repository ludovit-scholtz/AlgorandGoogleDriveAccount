using System.Text.Json;
using System.Text.Json.Serialization;
using BiatecOIDC.Model;
using BiatecSelfCustodyCore.Model;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;

namespace BiatecOIDC.BusinessLogic
{
    /// <inheritdoc cref="IBitcoinValuationService"/>
    /// <remarks>
    /// Fetches both BTC-USD and BCH-USD in one call (CoinGecko's "simple price" endpoint accepts a
    /// comma-separated <c>ids</c> list) and caches the pair as one JSON blob in <see cref="IDistributedCache"/>
    /// (shared, not per-user - a public market rate) for <see cref="BitcoinValuationConfiguration.CacheDurationMinutes"/> -
    /// same shared-cache shape as <see cref="CnbExchangeRateService"/>, just a much shorter TTL since a
    /// crypto spot price moves continuously rather than once a day.
    /// </remarks>
    public sealed class CoinGeckoValuationService : IBitcoinValuationService
    {
        private const string CacheKey = "oidc:fx:coingecko-btc-bch-usd";
        private const string BitcoinId = "bitcoin";
        private const string BitcoinCashId = "bitcoin-cash";
        private const long SatoshisPerCoin = 100_000_000L;

        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

        private readonly HttpClient _httpClient;
        private readonly IDistributedCache _cache;
        private readonly IOptionsMonitor<BitcoinValuationConfiguration> _config;
        private readonly ILogger<CoinGeckoValuationService> _logger;

        public CoinGeckoValuationService(
            HttpClient httpClient,
            IDistributedCache cache,
            IOptionsMonitor<BitcoinValuationConfiguration> config,
            ILogger<CoinGeckoValuationService> logger)
        {
            _httpClient = httpClient;
            _cache = cache;
            _config = config;
            _logger = logger;
        }

        public async Task<decimal> GetUsdValueAsync(BitcoinChainFamily family, long amountSatoshis, CancellationToken cancellationToken = default)
        {
            var prices = await GetUsdPricesAsync(cancellationToken);
            var usdPerCoin = family == BitcoinChainFamily.Bitcoin ? prices.BitcoinUsd : prices.BitcoinCashUsd;
            return usdPerCoin * amountSatoshis / SatoshisPerCoin;
        }

        private async Task<PricePair> GetUsdPricesAsync(CancellationToken cancellationToken)
        {
            var cached = await _cache.GetStringAsync(CacheKey, cancellationToken);
            if (!string.IsNullOrEmpty(cached))
            {
                try
                {
                    var deserialized = JsonSerializer.Deserialize<PricePair>(cached, JsonOptions);
                    if (deserialized != null && deserialized.BitcoinUsd > 0 && deserialized.BitcoinCashUsd > 0)
                    {
                        return deserialized;
                    }
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning(ex, "Corrupt cached CoinGecko BTC/BCH price pair; re-fetching.");
                }
            }

            return await FetchAndCacheAsync(cancellationToken);
        }

        private async Task<PricePair> FetchAndCacheAsync(CancellationToken cancellationToken)
        {
            CoinGeckoSimplePriceResponse? response;
            try
            {
                response = await _httpClient.GetFromJsonAsync<CoinGeckoSimplePriceResponse>(_config.CurrentValue.CoinGeckoSimplePriceUrl, JsonOptions, cancellationToken);
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
            {
                // Both prices are always fetched together, so which specific family the caller ultimately
                // wanted isn't known at this layer - Family is set to Bitcoin as a placeholder;
                // WalletController's 503 mapping doesn't distinguish by family anyway.
                throw new BitcoinValuationException(BitcoinChainFamily.Bitcoin, ex);
            }

            if (response == null
                || !response.TryGetValue(BitcoinId, out var btc) || btc.Usd <= 0
                || !response.TryGetValue(BitcoinCashId, out var bch) || bch.Usd <= 0)
            {
                throw new BitcoinValuationException(BitcoinChainFamily.Bitcoin, new InvalidOperationException("CoinGecko response did not include both a bitcoin and a bitcoin-cash USD price."));
            }

            var pair = new PricePair { BitcoinUsd = btc.Usd, BitcoinCashUsd = bch.Usd };
            var json = JsonSerializer.Serialize(pair, JsonOptions);
            await _cache.SetStringAsync(CacheKey, json, new DistributedCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(Math.Max(1, _config.CurrentValue.CacheDurationMinutes))
            }, cancellationToken);

            return pair;
        }

        private sealed class PricePair
        {
            public decimal BitcoinUsd { get; set; }
            public decimal BitcoinCashUsd { get; set; }
        }

        private sealed class CoinGeckoSimplePriceResponse : Dictionary<string, CoinGeckoUsdPrice>
        {
        }

        private sealed class CoinGeckoUsdPrice
        {
            [JsonPropertyName("usd")]
            public decimal Usd { get; set; }
        }
    }
}
