namespace BiatecOIDC.Model
{
    /// <summary>Bound from <c>SolanaValuation</c> - backs <see cref="BusinessLogic.CoinGeckoSolanaValuationService"/>.</summary>
    public class SolanaValuationConfiguration
    {
        /// <summary>CoinGecko's public "simple price" endpoint - no API key needed for this call shape.</summary>
        public string CoinGeckoSimplePriceUrl { get; set; } = "https://api.coingecko.com/api/v3/simple/price?ids=solana&vs_currencies=usd";

        /// <summary>How long a fetched SOL-USD spot price is trusted before re-fetching (a crypto spot price moves continuously).</summary>
        public int CacheDurationMinutes { get; set; } = 5;
    }
}
