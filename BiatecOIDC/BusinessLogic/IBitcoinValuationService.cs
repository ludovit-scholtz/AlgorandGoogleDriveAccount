using BiatecSelfCustodyCore.Model;

namespace BiatecOIDC.BusinessLogic
{
    /// <summary>
    /// Prices a Bitcoin-family native-asset spend in USD - unlike <see cref="IAssetValuationService"/> (which
    /// quotes an Algorand-network asset id against the Biatec Router), Bitcoin and Bitcoin Cash have no
    /// router to quote against and no asset id - the native coin *is* the asset, so this just needs a
    /// current BTC/BCH-USD spot price. See <see cref="CoinGeckoValuationService"/>.
    /// </summary>
    public interface IBitcoinValuationService
    {
        /// <exception cref="BitcoinValuationException">The current spot price could not be determined.</exception>
        Task<decimal> GetUsdValueAsync(BitcoinChainFamily family, long amountSatoshis, CancellationToken cancellationToken = default);
    }
}
