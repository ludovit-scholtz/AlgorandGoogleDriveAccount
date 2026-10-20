namespace BiatecMCP.BusinessLogic
{
    /// <summary>Outcome of a <c>sendTransaction</c> call - exactly one of <see cref="Signature"/> / <see cref="Error"/> is set.</summary>
    public sealed record SolanaSendResult(string? Signature, string? Error);

    /// <summary>
    /// The three Solana JSON-RPC calls BiatecMCP needs (balance, recent blockhash, broadcast) - an interface purely
    /// so the tools can be unit-tested without live HTTP; <see cref="SolanaRpcDataSource"/> is the real
    /// implementation. Every method fails soft (<c>null</c>/error result, never throws), like this repo's other
    /// public data sources.
    /// </summary>
    public interface ISolanaRpcDataSource
    {
        /// <summary>The address's native balance in lamports, or <c>null</c> if the node is unreachable/errors.</summary>
        Task<ulong?> TryGetBalanceAsync(string address, CancellationToken cancellationToken = default);

        /// <summary>A recent finalized blockhash (base58), or <c>null</c> if the node is unreachable/errors.</summary>
        Task<string?> TryGetLatestBlockhashAsync(CancellationToken cancellationToken = default);

        /// <summary>Broadcasts a fully-signed serialized transaction; returns its signature, or the node's error message.</summary>
        Task<SolanaSendResult> SendTransactionAsync(byte[] signedTransaction, CancellationToken cancellationToken = default);
    }
}
