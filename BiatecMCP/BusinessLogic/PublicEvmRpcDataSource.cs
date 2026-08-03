using System.Globalization;
using System.Numerics;
using System.Text.Json;
using Algorand;

namespace BiatecMCP.BusinessLogic
{
    /// <summary>
    /// Real HTTP implementation of <see cref="IPublicEvmRpcDataSource"/> - fetches
    /// https://chainid.network/chains.json and speaks raw JSON-RPC to a resolved chain's own node (no
    /// Nethereum.Web3/RPC package needed for the two calls this requires, <c>eth_chainId</c> and
    /// <c>eth_getBalance</c>). Not unit-tested at this level - see <see cref="IPublicEvmRpcDataSource"/>'s
    /// remarks; exercised via <see cref="EvmChainRegistry"/>'s tests against the mocked interface instead.
    /// </summary>
    public sealed class PublicEvmRpcDataSource : IPublicEvmRpcDataSource
    {
        private const string ChainListUrl = "https://chainid.network/chains.json";

        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

        public async Task<IReadOnlyList<EvmChainListEntry>> GetChainListAsync(CancellationToken cancellationToken = default)
        {
            using var httpClient = HttpClientConfigurator.ConfigureHttpClient(ChainListUrl, string.Empty);
            var json = await httpClient.GetStringAsync(ChainListUrl, cancellationToken);
            var rawEntries = JsonSerializer.Deserialize<List<RawEvmChainEntry>>(json, JsonOptions) ?? new List<RawEvmChainEntry>();

            return rawEntries
                .Select(e => new EvmChainListEntry
                {
                    Name = e.Name,
                    ChainId = e.ChainId,
                    ShortName = e.ShortName,
                    NativeCurrencySymbol = e.NativeCurrency?.Symbol ?? string.Empty,
                    NativeCurrencyDecimals = e.NativeCurrency?.Decimals ?? 18,
                    RpcCandidates = (e.Rpc ?? new List<string>())
                        .Where(url => url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) && !url.Contains("${", StringComparison.Ordinal))
                        .ToList()
                })
                .ToList();
        }

        public async Task<long?> TryGetLiveChainIdAsync(string rpcUrl, CancellationToken cancellationToken = default)
        {
            var result = await TryCallJsonRpcAsync(rpcUrl, "eth_chainId", Array.Empty<object>(), cancellationToken);
            if (result == null)
            {
                return null;
            }

            try
            {
                return Convert.ToInt64(result[2..], 16);
            }
            catch (Exception)
            {
                return null;
            }
        }

        public async Task<BigInteger?> TryGetBalanceAsync(string rpcUrl, string address, CancellationToken cancellationToken = default)
        {
            var result = await TryCallJsonRpcAsync(rpcUrl, "eth_getBalance", new object[] { address, "latest" }, cancellationToken);
            if (result == null)
            {
                return null;
            }

            try
            {
                // A leading "0" forces an unsigned parse - without it, a value whose top hex digit is >= 8
                // would otherwise be misread as negative by BigInteger.Parse(..., HexNumber).
                return BigInteger.Parse("0" + result[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static async Task<string?> TryCallJsonRpcAsync(string rpcUrl, string method, object[] parameters, CancellationToken cancellationToken)
        {
            try
            {
                using var httpClient = HttpClientConfigurator.ConfigureHttpClient(rpcUrl, string.Empty, timeout: 3000);
                var request = new JsonRpcRequest { Method = method, Params = parameters };
                using var response = await httpClient.PostAsJsonAsync(string.Empty, request, JsonOptions, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    return null;
                }

                var body = await response.Content.ReadFromJsonAsync<JsonRpcResponse>(JsonOptions, cancellationToken);
                var hexResult = body?.Result;
                return !string.IsNullOrEmpty(hexResult) && hexResult.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                    ? hexResult
                    : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private sealed class RawEvmChainEntry
        {
            public string Name { get; set; } = string.Empty;
            public long ChainId { get; set; }
            public string ShortName { get; set; } = string.Empty;
            public RawNativeCurrency? NativeCurrency { get; set; }
            public List<string>? Rpc { get; set; }
        }

        private sealed class RawNativeCurrency
        {
            public string Symbol { get; set; } = string.Empty;
            public int Decimals { get; set; } = 18;
        }

        private sealed class JsonRpcRequest
        {
            public string Jsonrpc { get; set; } = "2.0";
            public string Method { get; set; } = string.Empty;
            public object[] Params { get; set; } = Array.Empty<object>();
            public int Id { get; set; } = 1;
        }

        private sealed class JsonRpcResponse
        {
            public string? Result { get; set; }
        }
    }
}
