namespace BiatecMCP.BusinessLogic
{
    /// <summary>
    /// Fans a swap quote request out to every configured <see cref="IDexQuoteProvider"/> in parallel and
    /// compares the results - backs the <c>createSwapTransaction</c> MCP tool's "quote Biatec Router, Folks
    /// Router, and Haystack Router, then use the best one" behavior. A provider that throws or returns
    /// <c>null</c> is simply excluded from the comparison, never fatal to the overall request.
    /// </summary>
    public sealed class DexSwapAggregatorService
    {
        private readonly IReadOnlyList<IDexQuoteProvider> _providers;

        public DexSwapAggregatorService(IEnumerable<IDexQuoteProvider> providers)
        {
            _providers = providers.ToList();
        }

        /// <summary>Every configured provider - used by callers (e.g. <c>createSwapTransaction</c>) that need a specific provider's own capabilities beyond quoting (e.g. building a transaction).</summary>
        public IReadOnlyList<IDexQuoteProvider> Providers => _providers;

        /// <summary>Every provider's quote that succeeded - order is not significant, use <see cref="PickBest"/> to choose one.</summary>
        public async Task<IReadOnlyList<DexQuote>> GetAllQuotesAsync(long fromAsset, long toAsset, long amount, CancellationToken cancellationToken = default)
        {
            var tasks = _providers.Select(provider => GetQuoteSafeAsync(provider, fromAsset, toAsset, amount, cancellationToken));
            var results = await Task.WhenAll(tasks);
            return results.Where(quote => quote != null).Select(quote => quote!).ToList();
        }

        /// <summary>The quote with the highest <see cref="DexQuote.OutputAmount"/>, or <c>null</c> if <paramref name="quotes"/> is empty.</summary>
        public static DexQuote? PickBest(IReadOnlyList<DexQuote> quotes) =>
            quotes.OrderByDescending(quote => quote.OutputAmount).FirstOrDefault();

        private static async Task<DexQuote?> GetQuoteSafeAsync(IDexQuoteProvider provider, long fromAsset, long toAsset, long amount, CancellationToken cancellationToken)
        {
            try
            {
                return await provider.GetQuoteAsync(fromAsset, toAsset, amount, cancellationToken);
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
