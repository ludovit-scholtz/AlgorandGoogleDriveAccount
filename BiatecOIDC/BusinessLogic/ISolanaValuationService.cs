namespace BiatecOIDC.BusinessLogic
{
    /// <summary>
    /// Prices a native-SOL spend in USD - Solana has no Biatec Router to quote against and no asset id (the
    /// native coin *is* the asset), so this just needs a current SOL-USD spot price. See
    /// <see cref="CoinGeckoSolanaValuationService"/>.
    /// </summary>
    public interface ISolanaValuationService
    {
        /// <exception cref="SolanaValuationException">The current spot price could not be determined.</exception>
        Task<decimal> GetUsdValueAsync(ulong lamports, CancellationToken cancellationToken = default);
    }
}
