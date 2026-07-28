namespace BiatecOIDC.BusinessLogic
{
    /// <summary>
    /// Signs an Algorand transaction group on behalf of a self-custody wallet owner, enforcing that
    /// owner's configured spending limit (see <see cref="ISpendingLimitService"/>) on every payment/asset
    /// transfer in the group first. Backs <c>WalletController</c>'s <c>POST /wallet/sign</c>.
    /// </summary>
    public interface IWalletService
    {
        /// <summary>
        /// Validates every transaction in <paramref name="transactionsMsgPack"/> against the caller's
        /// spending limit, then signs each one via the shared self-custody signing path - in that order,
        /// so a group with one over-limit transaction never partially signs the others first.
        /// </summary>
        /// <param name="email">The wallet owner (from the caller's validated OIDC access token).</param>
        /// <param name="provider">
        /// The cloud storage provider the account is stored under (from the token's <c>biatec_idp</c>
        /// claim - never caller-supplied, so it can't be spoofed to point at the wrong storage backend).
        /// </param>
        /// <param name="transactionsMsgPack">One or more raw, unsigned (or partially-signed multisig) transactions, msgpack-encoded.</param>
        /// <param name="accessToken">The caller-supplied provider access token used to read/decrypt the self-custody account file.</param>
        /// <returns>The signed transactions, msgpack-encoded, in the same order as the input.</returns>
        /// <exception cref="SpendingLimitExceededException">A payment/asset transfer in the group exceeds the caller's configured limit.</exception>
        /// <exception cref="FormatException">A transaction could not be decoded.</exception>
        Task<IReadOnlyList<byte[]>> SignTransactionGroupAsync(string email, string provider, IReadOnlyList<byte[]> transactionsMsgPack, string? accessToken);
    }
}
