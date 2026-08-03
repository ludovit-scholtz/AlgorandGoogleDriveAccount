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
        /// Signs one or more unsigned transactions as an atomic group, as <paramref name="address"/>.
        /// Throws <see cref="WalletApiException"/> on any non-success response (e.g. spend limit exceeded,
        /// missing <c>sign</c>/<c>rekey</c> claim, no cached storage-provider access, or <paramref name="address"/>
        /// isn't a known/activated address - <c>address_not_active</c>).
        /// </summary>
        /// <param name="network">Which chain <paramref name="address"/> belongs to (e.g. <c>"algorand"</c>, <c>"voi"</c>).</param>
        /// <param name="address">Which identity signs - a native address (see <see cref="GetAddressAsync"/>) or one activated via <see cref="ActivateAddressAsync"/>.</param>
        Task<SignTransactionGroupResponse> SignAsync(string bearerToken, string network, string address, IReadOnlyList<byte[]> unsignedTransactions, CancellationToken cancellationToken = default);

        /// <summary>
        /// Lists every seed in the caller's vault (each seed's identifying address, creation date, and
        /// whether it's currently primary). Throws <see cref="WalletApiException"/> on failure.
        /// </summary>
        Task<ListSeedsResponse> ListSeedsAsync(string bearerToken, CancellationToken cancellationToken = default);

        /// <summary>
        /// Derives the address at <paramref name="slot"/> for the seed identified by
        /// <paramref name="seedAddress"/>, for every currently-supported chain family (AVM and EVM) in one
        /// call. Throws <see cref="WalletApiException"/> (e.g. <c>seed_not_found</c>) on failure.
        /// </summary>
        Task<DerivedAddressResponse> GetAddressAsync(string bearerToken, string seedAddress, int slot, CancellationToken cancellationToken = default);

        /// <summary>
        /// Reports whether BiatecOIDC currently knows which key signs for <paramref name="address"/> on
        /// <paramref name="network"/>. Throws <see cref="WalletApiException"/> on failure.
        /// </summary>
        Task<AddressInfoResponse> GetAddressInfoAsync(string bearerToken, string network, string address, CancellationToken cancellationToken = default);

        /// <summary>
        /// Registers that <paramref name="seedAddress"/>/<paramref name="slot"/>'s key signs for
        /// <paramref name="address"/> - the entry point for AVM rekey support. Throws
        /// <see cref="WalletApiException"/> (e.g. <c>rekey_not_confirmed</c> if an external address isn't
        /// yet rekeyed on-chain) on failure.
        /// </summary>
        Task<AddressInfoResponse> ActivateAddressAsync(string bearerToken, string network, string seedAddress, int slot, string address, CancellationToken cancellationToken = default);

        /// <summary>
        /// Lists every address currently resolvable to a signing seed/slot - every seed's own slot-0 AVM
        /// address plus every explicitly-activated entry (see <see cref="ActivateAddressAsync"/>). Throws
        /// <see cref="WalletApiException"/> on failure.
        /// </summary>
        Task<ListActiveAddressesResponse> ListActiveAddressesAsync(string bearerToken, CancellationToken cancellationToken = default);
    }
}
