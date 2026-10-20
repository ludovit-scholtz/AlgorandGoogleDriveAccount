using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using BiatecOIDC.Model;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;

namespace BiatecOIDC.BusinessLogic
{
    /// <inheritdoc cref="ISolanaValuationService"/>
    /// <remarks>
    /// Same shared-cache shape as <see cref="CoinGeckoValuationService"/> (a public market rate, not per-user),
    /// but its own endpoint, cache key and configuration section, so it is independent of the BTC/BCH pair that
    /// service fetches and caches - a deployment that overrides the Bitcoin URL needs no change for Solana.
    /// </remarks>
    public sealed class CoinGeckoSolanaValuationService : ISolanaValuationService
    {
        private const string CacheKey = "oidc:fx:coingecko-sol-usd";
        private const string SolanaId = "solana";
        private const decimal LamportsPerSol = 1_000_000_000m;

        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

        private readonly HttpClient _httpClient;
        private readonly IDistributedCache _cache;
        private readonly IOptionsMonitor<SolanaValuationConfiguration> _config;
        private readonly ILogger<CoinGeckoSolanaValuationService> _logger;

        public CoinGeckoSolanaValuationService(
            HttpClient httpClient,
            IDistributedCache cache,
            IOptionsMonitor<SolanaValuationConfiguration> config,
            ILogger<CoinGeckoSolanaValuationService> logger)
        {
            _httpClient = httpClient;
            _cache = cache;
            _config = config;
            _logger = logger;
        }

        public async Task<decimal> GetUsdValueAsync(ulong lamports, CancellationToken cancellationToken = default)
        {
            var usdPerSol = await GetUsdPerSolAsync(cancellationToken);
            return usdPerSol * lamports / LamportsPerSol;
        }

        private async Task<decimal> GetUsdPerSolAsync(CancellationToken cancellationToken)
        {
            var cached = await _cache.GetStringAsync(CacheKey, cancellationToken);
            if (!string.IsNullOrEmpty(cached))
            {
                if (decimal.TryParse(cached, NumberStyles.Number, CultureInfo.InvariantCulture, out var cachedPrice) && cachedPrice > 0)
                {
                    return cachedPrice;
                }

                _logger.LogWarning("Corrupt cached CoinGecko SOL price; re-fetching.");
            }

            CoinGeckoSimplePriceResponse? response;
            try
            {
                response = await _httpClient.GetFromJsonAsync<CoinGeckoSimplePriceResponse>(_config.CurrentValue.CoinGeckoSimplePriceUrl, JsonOptions, cancellationToken);
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
            {
                throw new SolanaValuationException(ex);
            }

            if (response == null || !response.TryGetValue(SolanaId, out var sol) || sol.Usd <= 0)
            {
                throw new SolanaValuationException(new InvalidOperationException("CoinGecko response did not include a positive solana USD price."));
            }

            await _cache.SetStringAsync(CacheKey, sol.Usd.ToString(CultureInfo.InvariantCulture), new DistributedCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(Math.Max(1, _config.CurrentValue.CacheDurationMinutes))
            }, cancellationToken);

            return sol.Usd;
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
