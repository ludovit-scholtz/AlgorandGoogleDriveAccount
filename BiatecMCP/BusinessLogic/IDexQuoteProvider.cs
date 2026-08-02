namespace BiatecMCP.BusinessLogic
{
    /// <summary>
    /// A single DEX/DEX-aggregator source <see cref="DexSwapAggregatorService"/> compares quotes across.
    /// Implementations must never throw for an ordinary "no route"/"unreachable" outcome - the aggregator
    /// treats a thrown exception the same as a <c>null</c> result (this provider is simply excluded from
    /// the comparison), so one broken/unreachable aggregator never blocks the others.
    /// </summary>
    public interface IDexQuoteProvider
    {
        /// <summary>Display name for this provider, echoed onto every <see cref="DexQuote"/> it returns.</summary>
        string ProviderName { get; }

        /// <summary>
        /// Quotes how much of <paramref name="toAsset"/> (base units) <paramref name="amount"/> base units
        /// of <paramref name="fromAsset"/> would currently buy. Asset id <c>0</c> is native ALGO. Returns
        /// <c>null</c> if this provider has no route/quote available right now.
        /// </summary>
        Task<DexQuote?> GetQuoteAsync(long fromAsset, long toAsset, long amount, CancellationToken cancellationToken = default);
    }
}
