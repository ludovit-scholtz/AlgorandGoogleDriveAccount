namespace BiatecOIDC.Model
{
    /// <summary>Bound from <c>BitcoinValuation</c> - backs <see cref="BusinessLogic.CoinGeckoValuationService"/>.</summary>
    public class BitcoinValuationConfiguration
    {
        /// <summary>CoinGecko's public "simple price" endpoint - no API key needed for this call shape.</summary>
        public string CoinGeckoSimplePriceUrl { get; set; } = "https://api.coingecko.com/api/v3/simple/price?ids=bitcoin,bitcoin-cash&vs_currencies=usd";

        /// <summary>
        /// How long a fetched BTC/BCH-USD spot price is trusted before re-fetching - deliberately much
        /// shorter than <see cref="ExchangeRateConfiguration.CacheDurationMinutes"/> (a fiat daily fixing):
        /// crypto spot prices move continuously, not once a day.
        /// </summary>
        public int CacheDurationMinutes { get; set; } = 5;
    }
}
