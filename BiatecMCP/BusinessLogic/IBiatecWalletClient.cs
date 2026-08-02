using BiatecMCP.Model;

namespace BiatecMCP.BusinessLogic
{
    /// <summary>
    /// Thin HTTP wrapper over BiatecOIDC's wallet REST API (<c>/wallet/sign</c>, <c>/wallet/seeds</c>).
    /// Every call forwards the caller's own Biatec bearer token unchanged - this service never holds, and
    /// never needs, any key material; BiatecOIDC does the actual signing (and spend-limit/rekey-claim
    /// enforcement) on the caller's behalf.
    /// </summary>
    public interface IBiatecWalletClient
    {
        /// <summary>
        /// Signs one or more unsigned transactions as an atomic group. Throws <see cref="WalletApiException"/>
        /// on any non-success response (e.g. spend limit exceeded, missing <c>sign</c>/<c>rekey</c> claim,
        /// no cached storage-provider access).
        /// </summary>
        Task<SignTransactionGroupResponse> SignAsync(string bearerToken, IReadOnlyList<byte[]> unsignedTransactions, CancellationToken cancellationToken = default);

        /// <summary>Lists every seed in the caller's vault. Throws <see cref="WalletApiException"/> on failure.</summary>
        Task<ListSeedsResponse> ListSeedsAsync(string bearerToken, CancellationToken cancellationToken = default);
    }
}
