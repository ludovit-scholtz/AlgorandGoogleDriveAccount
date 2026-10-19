namespace BiatecMCP.BusinessLogic
{
    /// <summary>
    /// Quotes via the Biatec Router (<c>router.api.biatec.io</c>, same NuGet-generated client
    /// <c>BiatecOIDC</c> already depends on for its own spend valuation) - the only provider
    /// <see cref="Model.CreateSwapTransactionResponse"/> can also build a real unsigned swap transaction
    /// for, via <see cref="BuildRouteTransactionsAsync"/> (see <c>createSwapTransaction</c>'s remarks for
    /// why Folks/Haystack are quote-only in this pass).
    /// </summary>
    public sealed class BiatecRouterQuoteProvider : IDexQuoteProvider
    {
        private readonly BiatecRouterConnector.BiatecRouterClient _client;

        public string ProviderName => "BiatecRouter";

        // Typed HttpClient (registered via AddHttpClient<BiatecRouterQuoteProvider>()) - same idiom as
        // BiatecOIDC's BiatecRouterQuoteClient. BiatecRouterClient defaults its BaseUrl to
        // https://router.api.biatec.io.
        public BiatecRouterQuoteProvider(HttpClient httpClient)
        {
            _client = new BiatecRouterConnector.BiatecRouterClient(httpClient);
        }

        public async Task<DexQuote?> GetQuoteAsync(long fromAsset, long toAsset, long amount, CancellationToken cancellationToken = default)
        {
            var output = checked((long)await _client.Api.QuoteAsync(checked((ulong)fromAsset), checked((ulong)toAsset), checked((ulong)amount), cancellationToken));
            return new DexQuote { ProviderName = ProviderName, OutputAmount = output };
        }

        /// <summary>
        /// Builds real unsigned swap transaction(s) for the best route Biatec Router finds, via its
        /// <c>RouteTxsAsync</c> endpoint. Returns the base64-encoded transactions to sign, in submission
        /// order, or an empty list if the router returned no routes.
        /// </summary>
        public async Task<IReadOnlyList<string>> BuildRouteTransactionsAsync(
            string sender,
            long fromAsset,
            long toAsset,
            long swapAmount,
            long receiveMinimum,
            BiatecRouterConnector.Generated.TransactionParametersResponse transactionParameters,
            CancellationToken cancellationToken = default)
        {
            var input = new BiatecRouterConnector.Generated.RouteInputParameters
            {
                Sender = sender,
                FromAsset = checked((ulong)fromAsset),
                ToAsset = checked((ulong)toAsset),
                SwapAmount = checked((ulong)swapAmount),
                ReceiveMinimum = checked((ulong)receiveMinimum),
                RoutesCount = 1,
                TransParams = transactionParameters
            };

            var cover = await _client.Api.RouteTxsAsync(input, cancellationToken);
            var bestRoute = cover.Routes?.FirstOrDefault();
            return bestRoute?.TxsToSign?.ToList() ?? new List<string>();
        }
    }
}
