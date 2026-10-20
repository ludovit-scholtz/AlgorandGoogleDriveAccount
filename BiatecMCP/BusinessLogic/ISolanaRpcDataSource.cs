namespace BiatecMCP.BusinessLogic
{
    /// <summary>
    /// Outcome of a <c>sendTransaction</c> call - exactly one of <see cref="Signature"/> / <see cref="Error"/> is set.
    /// When <see cref="OutcomeUnknown"/> is <c>true</c> the request failed in a way that does not prove the node
    /// rejected it (timeout, connection drop, non-success HTTP status): the transaction may have been accepted, and
    /// <see cref="ExpectedSignature"/> (computed locally from the signed bytes) says what to look up.
    /// </summary>
    public sealed record SolanaSendResult(string? Signature, string? Error, bool OutcomeUnknown = false, string? ExpectedSignature = null);

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

        /// <summary>A recent blockhash (base58) at <c>confirmed</c> commitment - the same level the balance pre-check and broadcast preflight use - or <c>null</c> if the node is unreachable/errors.</summary>
        Task<string?> TryGetLatestBlockhashAsync(CancellationToken cancellationToken = default);

        /// <summary>Broadcasts a fully-signed serialized transaction; returns its signature, or the node's error message (or an outcome-unknown result - see <see cref="SolanaSendResult"/>).</summary>
        Task<SolanaSendResult> SendTransactionAsync(byte[] signedTransaction, CancellationToken cancellationToken = default);
    }
}
