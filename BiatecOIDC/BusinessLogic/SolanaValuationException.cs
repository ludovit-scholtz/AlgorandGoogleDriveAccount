namespace BiatecOIDC.BusinessLogic
{
    /// <summary>
    /// Thrown by <see cref="ISolanaValuationService"/> when a SOL-USD spot price can't be fetched (CoinGecko
    /// unreachable, or missing from its response). Every Solana transfer is subject to the spending limit, so
    /// this fails the whole sign request closed (mapped to 503 by <c>WalletController</c>) rather than silently
    /// treating the spend as zero-value.
    /// </summary>
    public sealed class SolanaValuationException : Exception
    {
        public SolanaValuationException(Exception innerException)
            : base("Unable to determine the current USD value of SOL via CoinGecko.", innerException)
        {
        }
    }
}
