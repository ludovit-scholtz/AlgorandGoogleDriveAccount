namespace BiatecOIDC.BusinessLogic
{
    /// <summary>
    /// Signs an Algorand transaction group on behalf of a self-custody wallet owner, enforcing that
    /// owner's configured daily/weekly/monthly spending limit (see <see cref="ISpendingLimitService"/>) on
    /// the group's total USD value first. Backs <c>WalletController</c>'s <c>POST /wallet/sign</c>.
    /// </summary>
    public interface IWalletService
    {
        /// <summary>
        /// Prices every payment/asset-transfer in <paramref name="transactionsMsgPack"/> in USD (via
        /// <see cref="IAssetValuationService"/>), checks the group's total against the caller's
        /// daily/weekly/monthly limits, then signs each transaction via the shared self-custody signing
        /// path, and finally records the spend to the caller's ledger - in that order, so a group that
        /// would exceed a limit never partially signs.
        /// </summary>
        /// <param name="email">The wallet owner (from the caller's validated OIDC access token).</param>
        /// <param name="provider">
        /// The cloud storage provider the account is stored under (from the token's <c>biatec_idp</c>
        /// claim - never caller-supplied, so it can't be spoofed to point at the wrong storage backend).
        /// </param>
        /// <param name="transactionsMsgPack">One or more raw, unsigned (or partially-signed multisig) transactions, msgpack-encoded.</param>
        /// <param name="accessToken">The caller-supplied provider access token used to read/decrypt the self-custody account file and the spending-limit data.</param>
        /// <param name="primaryAddress">
        /// Selects which seed signs (<c>null</c> = the vault's current primary seed) - resolved once, up
        /// front, via <c>ICloudAccountRepository.ResolveSeedAddressAsync</c>, and used consistently for
        /// both the spending-limit check and every transaction's actual signing, so a concurrent
        /// <c>PUT /wallet/seeds/primary</c> mid-request can't cause them to disagree.
        /// </param>
        /// <param name="slot">ARC-76 derivation index within the selected seed.</param>
        /// <returns>The signed transactions, msgpack-encoded, in the same order as the input.</returns>
        /// <exception cref="AssetValuationException">A spent asset's USD value could not be determined.</exception>
        /// <exception cref="SpendingLimitExceededException">The group's total spend exceeds a configured global or address-specific daily/weekly/monthly limit.</exception>
        /// <exception cref="FormatException">A transaction could not be decoded.</exception>
        /// <exception cref="InvalidOperationException"><paramref name="primaryAddress"/> is given but no seed in the vault has that address.</exception>
        Task<IReadOnlyList<byte[]>> SignTransactionGroupAsync(string email, string provider, IReadOnlyList<byte[]> transactionsMsgPack, string? accessToken, string? primaryAddress = null, int slot = 0);
    }
}
