namespace BiatecMCP.BusinessLogic
{
    /// <summary>
    /// Placeholder for Haystack Router quoting. Unlike Biatec Router and Folks Router, Haystack Router's
    /// public REST contract could not be confirmed while building this integration (only a JS/TS SDK and
    /// narrative documentation were found, no verified plain-HTTP request/response shape) - rather than
    /// guess at an endpoint and risk a silently-wrong quote influencing which route
    /// <c>createSwapTransaction</c> recommends, this provider always reports "no quote available" until a
    /// real REST (or verified SDK-equivalent) contract is confirmed and wired in. It's still registered
    /// (rather than omitted) so the provider list, and the "3 aggregators compared" framing, doesn't need to
    /// change once it's implemented for real.
    /// </summary>
    public sealed class HaystackRouterQuoteProvider : IDexQuoteProvider
    {
        public string ProviderName => "HaystackRouter";

        public Task<DexQuote?> GetQuoteAsync(long fromAsset, long toAsset, long amount, CancellationToken cancellationToken = default) =>
            Task.FromResult<DexQuote?>(null);
    }
}
