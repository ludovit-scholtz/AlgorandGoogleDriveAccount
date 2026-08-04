using BiatecSelfCustodyCore.Model;

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
        /// <param name="seedAddress">
        /// Selects which seed signs (<c>null</c> = the vault's current primary seed) - resolved once, up
        /// front, via <c>ICloudAccountRepository.ResolveSeedAddressAsync</c>, and used consistently for
        /// both the spending-limit check and every transaction's actual signing, so a concurrent
        /// <c>PUT /wallet/seeds/primary</c> mid-request can't cause them to disagree.
        /// </param>
        /// <param name="slot">ARC-76 derivation index within the selected seed.</param>
        /// <param name="applySpendingLimits">
        /// Whether to price the group via the Biatec Router and enforce the spending limit at all - pass
        /// <c>false</c> for any Algorand-family network other than mainnet (<c>WalletController</c> decides
        /// this from the resolved network's genesis id). The Biatec Router - and therefore both asset
        /// valuation and the spending limit it feeds - is only deployed on Algorand mainnet; attempting it
        /// on testnet/Voi/Aramid/etc. would fail every transfer closed with a confusing valuation error
        /// rather than signing a transaction nothing is actually wrong with. Defaults to <c>true</c> so
        /// existing callers keep today's behavior unless they opt out explicitly.
        /// </param>
        /// <returns>The signed transactions, msgpack-encoded, in the same order as the input.</returns>
        /// <exception cref="AssetValuationException">A spent asset's USD value could not be determined (only possible when <paramref name="applySpendingLimits"/> is <c>true</c>).</exception>
        /// <exception cref="SpendingLimitExceededException">The group's total spend exceeds a configured global or address-specific daily/weekly/monthly limit (only possible when <paramref name="applySpendingLimits"/> is <c>true</c>).</exception>
        /// <exception cref="FormatException">A transaction could not be decoded.</exception>
        /// <exception cref="InvalidOperationException"><paramref name="seedAddress"/> is given but no seed in the vault has that address.</exception>
        Task<IReadOnlyList<byte[]>> SignTransactionGroupAsync(string email, string provider, IReadOnlyList<byte[]> transactionsMsgPack, string? accessToken, string? seedAddress = null, int slot = 0, bool applySpendingLimits = true);

        /// <summary>
        /// Signs one or more unsigned EVM (Ethereum-family) transactions via the shared self-custody
        /// signing path (<see cref="BiatecSelfCustodyCore.BusinessLogic.IDriveService.SignEvmTransactionAsync"/>).
        /// Unlike <see cref="SignTransactionGroupAsync"/>, this does **not** price or spending-limit-check
        /// anything - EVM native-currency spending limits aren't implemented yet (same current scope as
        /// every AVM chain other than Algorand mainnet - see <c>chains.html</c>'s capability matrix), so
        /// there is nothing to enforce here today.
        /// </summary>
        /// <param name="email">The wallet owner (from the caller's validated OIDC access token).</param>
        /// <param name="provider">The cloud storage provider the account is stored under.</param>
        /// <param name="unsignedTransactions">One or more unsigned EVM transactions.</param>
        /// <param name="accessToken">The caller-supplied provider access token used to read/decrypt the self-custody account file.</param>
        /// <param name="seedAddress">Selects which seed signs (<c>null</c> = the vault's current primary seed).</param>
        /// <param name="slot">ARC-76 derivation index within the selected seed.</param>
        /// <returns>The RLP-encoded, fully-signed transactions, in the same order as the input.</returns>
        /// <exception cref="FormatException">A transaction could not be built/signed.</exception>
        /// <exception cref="InvalidOperationException"><paramref name="seedAddress"/> is given but no seed in the vault has that address.</exception>
        Task<IReadOnlyList<byte[]>> SignEvmTransactionGroupAsync(string email, string provider, IReadOnlyList<EvmUnsignedTransaction> unsignedTransactions, string? accessToken, string? seedAddress = null, int slot = 0);

        /// <summary>
        /// Signs a single unsigned Bitcoin or Bitcoin Cash transaction via the shared self-custody signing
        /// path (<see cref="BiatecSelfCustodyCore.BusinessLogic.IDriveService.SignBitcoinTransactionAsync"/>),
        /// after pricing every non-change output (<see cref="BitcoinTransactionOutput.IsChange"/>) via
        /// <see cref="IBitcoinValuationService"/> and checking the total against the caller's
        /// daily/weekly/monthly limits - the same enforcement <see cref="SignTransactionGroupAsync"/> applies
        /// for Algorand, just priced directly (the native coin *is* the asset, no router needed) instead of
        /// via the Biatec Router.
        /// </summary>
        /// <param name="email">The wallet owner (from the caller's validated OIDC access token).</param>
        /// <param name="provider">The cloud storage provider the account is stored under.</param>
        /// <param name="family">Bitcoin or Bitcoin Cash.</param>
        /// <param name="transaction">The unsigned transaction - inputs (this seed/slot's own UTXOs) and outputs (recipient(s) plus change).</param>
        /// <param name="accessToken">The caller-supplied provider access token used to read/decrypt the self-custody account file and the spending-limit data.</param>
        /// <param name="seedAddress">Selects which seed signs (<c>null</c> = the vault's current primary seed).</param>
        /// <param name="slot">ARC-76 derivation index within the selected seed.</param>
        /// <returns>The fully-signed transaction's raw bytes.</returns>
        /// <exception cref="BitcoinValuationException">The current BTC/BCH-USD spot price could not be determined.</exception>
        /// <exception cref="SpendingLimitExceededException">The transaction's total spend exceeds a configured global or address-specific daily/weekly/monthly limit.</exception>
        /// <exception cref="FormatException">The transaction could not be built/signed.</exception>
        /// <exception cref="InvalidOperationException"><paramref name="seedAddress"/> is given but no seed in the vault has that address.</exception>
        Task<byte[]> SignBitcoinTransactionGroupAsync(string email, string provider, BitcoinChainFamily family, BitcoinUnsignedTransaction transaction, string? accessToken, string? seedAddress = null, int slot = 0);
    }
}
