using System.Text.Json;

namespace BiatecMCP.BusinessLogic
{
    /// <summary>
    /// Quotes via Folks Router's public REST API (<c>https://api.folksrouter.io</c>) - quote-only, this
    /// provider never builds a transaction (see <c>createSwapTransaction</c>'s remarks: only Biatec Router's
    /// route can currently be turned into a real transaction from BiatecMCP). Response field names are
    /// parsed defensively (a small set of candidate names tried in order) since Folks Router's exact JSON
    /// contract isn't pinned to a versioned schema here - an unexpected shape is treated as "no quote
    /// available" (returns <c>null</c>), the same as an unreachable/erroring request, rather than throwing.
    /// </summary>
    public sealed class FolksRouterQuoteProvider : IDexQuoteProvider
    {
        private static readonly string[] OutputAmountFieldCandidates = { "outputAmount", "toAmount", "amountOut", "outAmount" };

        private readonly HttpClient _httpClient;

        // Typed HttpClient (registered via AddHttpClient<FolksRouterQuoteProvider>()), BaseAddress configured
        // to https://api.folksrouter.io in Program.cs.
        public FolksRouterQuoteProvider(HttpClient httpClient)
        {
            _httpClient = httpClient;
        }

        public string ProviderName => "FolksRouter";

        public async Task<DexQuote?> GetQuoteAsync(long fromAsset, long toAsset, long amount, CancellationToken cancellationToken = default)
        {
            var requestUri = $"v1/fetch/quote?network=mainnet&fromAsset={fromAsset}&toAsset={toAsset}&amount={amount}&type=fixed-input";
            using var response = await _httpClient.GetAsync(requestUri, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

            foreach (var fieldName in OutputAmountFieldCandidates)
            {
                if (document.RootElement.TryGetProperty(fieldName, out var value) && value.TryGetInt64(out var outputAmount))
                {
                    return new DexQuote { ProviderName = ProviderName, OutputAmount = outputAmount };
                }
            }

            return null;
        }
    }
}
