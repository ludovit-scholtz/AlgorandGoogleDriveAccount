using System.Text.Json;
using Algorand;
using Algorand.Algod;

namespace BiatecMCP.BusinessLogic
{
    /// <summary>
    /// Real HTTP implementation of <see cref="IPublicAlgodDataSource"/> - fetches
    /// https://scholtz.github.io/AlgorandPublicData's genesis list and per-chain public-algod-provider
    /// lists, and checks a candidate node's liveness by calling its own <c>/v2/transactions/params</c>
    /// (reusing the Algorand4 SDK's own model/deserialization, since the response shape is identical).
    /// Not unit-tested at this level - see <see cref="IPublicAlgodDataSource"/>'s remarks; exercised via
    /// <see cref="AlgorandChainRegistry"/>'s tests against the mocked interface instead.
    /// </summary>
    public sealed class PublicAlgodDataSource : IPublicAlgodDataSource
    {
        private const string GenesisListUrl = "https://scholtz.github.io/AlgorandPublicData/genesis/genesis-list.json";
        private const string ProvidersBaseUrl = "https://scholtz.github.io/AlgorandPublicData/algod/";

        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

        public async Task<IReadOnlyList<GenesisListEntry>> GetGenesisListAsync(CancellationToken cancellationToken = default)
        {
            using var httpClient = HttpClientConfigurator.ConfigureHttpClient(GenesisListUrl, string.Empty);
            var json = await httpClient.GetStringAsync(GenesisListUrl, cancellationToken);
            return JsonSerializer.Deserialize<List<GenesisListEntry>>(json, JsonOptions) ?? new List<GenesisListEntry>();
        }

        public async Task<IReadOnlyList<PublicAlgodProvider>> GetProvidersAsync(string genesisId, CancellationToken cancellationToken = default)
        {
            var url = $"{ProvidersBaseUrl}{genesisId}/public-algod-providers.json";
            using var httpClient = HttpClientConfigurator.ConfigureHttpClient(url, string.Empty);
            try
            {
                var json = await httpClient.GetStringAsync(url, cancellationToken);
                return JsonSerializer.Deserialize<List<PublicAlgodProvider>>(json, JsonOptions) ?? new List<PublicAlgodProvider>();
            }
            catch (HttpRequestException)
            {
                return new List<PublicAlgodProvider>();
            }
        }

        public async Task<string?> TryGetLiveGenesisHashAsync(PublicAlgodProvider provider, CancellationToken cancellationToken = default)
        {
            try
            {
                using var httpClient = HttpClientConfigurator.ConfigureHttpClient(provider.AlgodHost, provider.Token, provider.Header, timeout: 3000);
                var algodApi = new DefaultApi(httpClient);
                var response = await algodApi.TransactionParamsAsync(cancellationToken);
                return Convert.ToBase64String(response.GenesisHash);
            }
            catch (Exception)
            {
                // Unreachable/erroring node - not live, per IPublicAlgodDataSource's contract this never throws.
                return null;
            }
        }
    }
}
