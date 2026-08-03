namespace BiatecMCP.BusinessLogic
{
    /// <summary>Chain slugs Blockchair's API uses - the "chain" path segment of every endpoint.</summary>
    public static class BlockchairChainSlugs
    {
        public const string Bitcoin = "bitcoin";
        public const string BitcoinCash = "bitcoin-cash";
    }

    /// <summary>One spendable UTXO at an address.</summary>
    public sealed record BitcoinUtxo(string TxId, uint Vout, long AmountSatoshis);

    /// <summary>
    /// Raw access to a public Bitcoin/Bitcoin Cash block explorer API (Blockchair - one unified API shape
    /// for both chains, see <see cref="BlockchairDataSource"/>) - separated from
    /// <see cref="Helper.BitcoinTransactionBuilder"/>'s coin-selection/fee-estimation logic purely so that
    /// logic can be unit-tested with canned data instead of live HTTP calls, same seam this repo already
    /// uses for <c>IPublicAlgodDataSource</c>/<c>IPublicEvmRpcDataSource</c>. <see cref="BlockchairDataSource"/>
    /// is the real implementation - its exact request/response shapes are documented from Blockchair's
    /// public API reference, not exercised against a live endpoint in this repo's test/build environment
    /// (no outbound network access here), so treat it as needing manual/E2E verification before relying on
    /// it for a real transfer, same precedent as this repo's other leaf HTTP providers.
    /// </summary>
    public interface IPublicBitcoinDataSource
    {
        /// <summary>Every currently-unspent output at <paramref name="address"/>.</summary>
        Task<IReadOnlyList<BitcoinUtxo>> GetUtxosAsync(string chainSlug, string address, CancellationToken cancellationToken = default);

        /// <summary>The address's current confirmed balance, in satoshis - <c>null</c> if unreachable/errors, never throws.</summary>
        Task<long?> TryGetBalanceAsync(string chainSlug, string address, CancellationToken cancellationToken = default);

        /// <summary>Blockchair's own suggested fee rate, in satoshis per byte - <c>null</c> if unreachable/errors, never throws.</summary>
        Task<decimal?> TryGetSuggestedFeeRateAsync(string chainSlug, CancellationToken cancellationToken = default);

        /// <summary>Broadcasts a fully-signed raw transaction (hex-encoded). Returns the accepted transaction's id, or <c>null</c> if the broadcast failed - never throws.</summary>
        Task<string?> TryBroadcastAsync(string chainSlug, string rawTransactionHex, CancellationToken cancellationToken = default);
    }
}
