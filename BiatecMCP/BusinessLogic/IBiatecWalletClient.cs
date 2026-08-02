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
        /// <param name="primaryAddress">Which seed signs (its own identifying slot-0 address). <c>null</c> = the vault's current primary seed.</param>
        /// <param name="slot">ARC-76 derivation index within the selected seed.</param>
        Task<SignTransactionGroupResponse> SignAsync(string bearerToken, IReadOnlyList<byte[]> unsignedTransactions, string? primaryAddress = null, int slot = 0, CancellationToken cancellationToken = default);

        /// <summary>Lists every seed in the caller's vault. Throws <see cref="WalletApiException"/> on failure.</summary>
        Task<ListSeedsResponse> ListSeedsAsync(string bearerToken, CancellationToken cancellationToken = default);

        /// <summary>Lists every seed's identifying address in the caller's vault, and which one is primary. Throws <see cref="WalletApiException"/> on failure.</summary>
        Task<ListAddressesResponse> ListAddressesAsync(string bearerToken, CancellationToken cancellationToken = default);

        /// <summary>
        /// Derives the ARC-76 address at <paramref name="slot"/> for the seed identified by
        /// <paramref name="primaryAddress"/>. Throws <see cref="WalletApiException"/> (e.g. <c>seed_not_found</c>) on failure.
        /// </summary>
        Task<DerivedAddressResponse> GetAddressAsync(string bearerToken, string primaryAddress, int slot, CancellationToken cancellationToken = default);
    }
}
