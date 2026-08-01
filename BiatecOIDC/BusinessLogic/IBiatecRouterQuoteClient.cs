namespace BiatecOIDC.BusinessLogic
{
    /// <summary>
    /// Thin seam around the generated <c>BiatecRouterConnector</c> client, so
    /// <see cref="BiatecRouterValuationService"/> can be unit-tested without a live HTTP call to
    /// <c>router.api.biatec.io</c>. The only capability used is the router's public, unauthenticated
    /// <c>/quote</c> endpoint - pricing, not swap execution.
    /// </summary>
    public interface IBiatecRouterQuoteClient
    {
        /// <summary>
        /// Quotes how much of <paramref name="toAsset"/> (in its own base units) <paramref name="amount"/>
        /// base units of <paramref name="fromAsset"/> would currently swap for, via the Biatec Router's
        /// aggregated liquidity. Asset id <c>0</c> is native ALGO. Throws if the router has no route
        /// between the two assets or is unreachable.
        /// </summary>
        Task<long> QuoteAsync(long fromAsset, long toAsset, long amount, CancellationToken cancellationToken = default);
    }
}
