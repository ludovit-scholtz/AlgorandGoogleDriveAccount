using BiatecRouterConnector;

namespace BiatecOIDC.BusinessLogic
{
    /// <inheritdoc cref="IBiatecRouterQuoteClient"/>
    public sealed class BiatecRouterQuoteClient : IBiatecRouterQuoteClient
    {
        private readonly BiatecRouterClient _client;

        // Typed HttpClient (registered via AddHttpClient<BiatecRouterQuoteClient>()) - BiatecRouterClient
        // defaults its BaseUrl to https://router.api.biatec.io. No Authorization header is supplied since
        // /quote is a public read-only endpoint (unlike RouteTxsAsync, which needs an ARC-0014 auth
        // transaction to build actual swap transactions - not used here).
        public BiatecRouterQuoteClient(HttpClient httpClient)
        {
            _client = new BiatecRouterClient(httpClient);
        }

        public Task<long> QuoteAsync(long fromAsset, long toAsset, long amount, CancellationToken cancellationToken = default)
        {
            return _client.Api.QuoteAsync(fromAsset, toAsset, amount, cancellationToken);
        }
    }
}
