using System.Text.Json;
using System.Text.Json.Serialization;

namespace BiatecMCP.BusinessLogic
{
    /// <inheritdoc cref="IPublicBitcoinDataSource"/>
    /// <remarks>
    /// Blockchair (https://blockchair.com) exposes the same REST shape across every chain it supports,
    /// parameterized only by the <c>{chain}</c> path segment (<see cref="BlockchairChainSlugs"/>) - one
    /// implementation covers both Bitcoin and Bitcoin Cash, unlike a chain-specific explorer API. No API
    /// key is required for the low-volume calls this makes. Every method fails soft (returns
    /// <c>null</c>/empty, logs a warning) rather than throwing, consistent with this repo's other public
    /// data sources (<c>PublicAlgodDataSource</c>/<c>PublicEvmRpcDataSource</c>) - callers decide what "data
    /// unavailable" means for their own request.
    /// </remarks>
    public sealed class BlockchairDataSource : IPublicBitcoinDataSource
    {
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

        private readonly HttpClient _httpClient;
        private readonly ILogger<BlockchairDataSource> _logger;

        public BlockchairDataSource(HttpClient httpClient, ILogger<BlockchairDataSource> logger)
        {
            _httpClient = httpClient;
            _logger = logger;
        }

        public async Task<IReadOnlyList<BitcoinUtxo>> GetUtxosAsync(string chainSlug, string address, CancellationToken cancellationToken = default)
        {
            try
            {
                address = NormalizeAddress(address);
                var response = await _httpClient.GetFromJsonAsync<DashboardResponse>(
                    $"{Uri.EscapeDataString(chainSlug)}/dashboards/address/{Uri.EscapeDataString(address)}?limit=0,200", JsonOptions, cancellationToken);

                var entry = response?.Data?.GetValueOrDefault(address);
                if (entry?.Utxo == null)
                {
                    return Array.Empty<BitcoinUtxo>();
                }

                return entry.Utxo
                    .Where(u => !string.IsNullOrEmpty(u.TransactionHash) && u.Value > 0)
                    .Select(u => new BitcoinUtxo(u.TransactionHash!, (uint)u.Index, u.Value))
                    .ToList();
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
            {
                _logger.LogWarning(ex, "Failed to fetch UTXOs for {Address} on {Chain} from Blockchair.", address, chainSlug);
                return Array.Empty<BitcoinUtxo>();
            }
        }

        public async Task<long?> TryGetBalanceAsync(string chainSlug, string address, CancellationToken cancellationToken = default)
        {
            try
            {
                address = NormalizeAddress(address);
                var response = await _httpClient.GetFromJsonAsync<DashboardResponse>(
                    $"{Uri.EscapeDataString(chainSlug)}/dashboards/address/{Uri.EscapeDataString(address)}", JsonOptions, cancellationToken);

                return response?.Data?.GetValueOrDefault(address)?.Address?.Balance;
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
            {
                _logger.LogWarning(ex, "Failed to fetch the balance for {Address} on {Chain} from Blockchair.", address, chainSlug);
                return null;
            }
        }

        public async Task<decimal?> TryGetSuggestedFeeRateAsync(string chainSlug, CancellationToken cancellationToken = default)
        {
            try
            {
                var response = await _httpClient.GetFromJsonAsync<StatsResponse>($"{Uri.EscapeDataString(chainSlug)}/stats", JsonOptions, cancellationToken);
                var satPerByte = response?.Data?.SuggestedTransactionFeePerByteSat;
                return satPerByte is > 0 ? satPerByte : null;
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
            {
                _logger.LogWarning(ex, "Failed to fetch the suggested fee rate for {Chain} from Blockchair.", chainSlug);
                return null;
            }
        }

        public async Task<string?> TryBroadcastAsync(string chainSlug, string rawTransactionHex, CancellationToken cancellationToken = default)
        {
            try
            {
                using var response = await _httpClient.PostAsJsonAsync($"{Uri.EscapeDataString(chainSlug)}/push/transaction", new { data = rawTransactionHex }, JsonOptions, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Blockchair rejected a {Chain} broadcast: {StatusCode}.", chainSlug, response.StatusCode);
                    return null;
                }

                var payload = await response.Content.ReadFromJsonAsync<PushTransactionResponse>(JsonOptions, cancellationToken);
                return payload?.Data?.TransactionHash;
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
            {
                _logger.LogWarning(ex, "Failed to broadcast a {Chain} transaction via Blockchair.", chainSlug);
                return null;
            }
        }

        /// <summary>
        /// Strips the <c>"bitcoincash:"</c> URI-scheme prefix from a CashAddr, if present. Every Bitcoin
        /// Cash address this codebase derives (<c>CloudAccountRepository.DeriveBitcoinCashAddressAsync</c>,
        /// via <c>NBitcoin.Altcoins.BCash</c>'s <c>ToString()</c>) includes this prefix, but Blockchair's
        /// REST API - both in its URL path segment and in the response <c>data</c> dictionary's own key -
        /// uses the bare CashAddr payload without it; sending the prefixed form 404s the request (silently
        /// surfaced to callers as "Could not reach the block explorer", indistinguishable from a genuine
        /// outage). A Bitcoin (BTC) address never has this prefix, so this is a no-op for that chain -
        /// every address that reaches this class is normalized here rather than requiring every caller
        /// (<c>getCryptoBalance</c>, <c>createBitcoinTransaction</c>, <c>executeBitcoinTransaction</c>) to
        /// know about the quirk.
        /// </summary>
        private static string NormalizeAddress(string address) =>
            address.StartsWith("bitcoincash:", StringComparison.OrdinalIgnoreCase) ? address["bitcoincash:".Length..] : address;

        private sealed class DashboardResponse
        {
            public Dictionary<string, AddressDashboard>? Data { get; set; }
        }

        private sealed class AddressDashboard
        {
            public AddressInfo? Address { get; set; }
            public List<UtxoEntry>? Utxo { get; set; }
        }

        private sealed class AddressInfo
        {
            public long Balance { get; set; }
        }

        private sealed class UtxoEntry
        {
            [JsonPropertyName("transaction_hash")]
            public string? TransactionHash { get; set; }

            public int Index { get; set; }

            public long Value { get; set; }
        }

        private sealed class StatsResponse
        {
            public StatsData? Data { get; set; }
        }

        private sealed class StatsData
        {
            [JsonPropertyName("suggested_transaction_fee_per_byte_sat")]
            public decimal? SuggestedTransactionFeePerByteSat { get; set; }
        }

        private sealed class PushTransactionResponse
        {
            public PushTransactionData? Data { get; set; }
        }

        private sealed class PushTransactionData
        {
            [JsonPropertyName("transaction_hash")]
            public string? TransactionHash { get; set; }
        }
    }
}
