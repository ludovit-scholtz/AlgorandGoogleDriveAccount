using BiatecSelfCustodyCore.Model;

namespace BiatecOIDC.BusinessLogic
{
    /// <summary>
    /// Thrown by <see cref="IBitcoinValuationService"/> when a BTC/BCH-USD spot price can't be fetched
    /// (CoinGecko unreachable, or missing from its response). Every Bitcoin-family transfer is subject to
    /// the spending limit, so this fails the whole sign request closed (mapped to 503 by
    /// <c>WalletController</c>) rather than silently treating the spend as zero-value.
    /// </summary>
    public sealed class BitcoinValuationException : Exception
    {
        public BitcoinChainFamily Family { get; }

        public BitcoinValuationException(BitcoinChainFamily family, Exception innerException)
            : base($"Unable to determine the current USD value of {family} via CoinGecko.", innerException)
        {
            Family = family;
        }
    }
}
