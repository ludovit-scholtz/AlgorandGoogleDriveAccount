namespace BiatecOIDC.BusinessLogic
{
    /// <summary>
    /// Per-user (by email) wallet spending limit, enforced by <see cref="IWalletService"/> on every
    /// payment/asset-transfer signed via <c>POST /wallet/sign</c>. Global per user - not per relying-party
    /// client - so every application holding a <c>sign</c>-scoped token for a given user is bound by the
    /// same ceiling that user has configured.
    /// </summary>
    public interface ISpendingLimitService
    {
        /// <summary>
        /// The maximum amount (microAlgos for a payment, base units for an asset transfer) allowed in a
        /// single transaction. <c>0</c> means unbounded (no limit configured) - the same convention
        /// <c>TransferPolicy.ExceedsMaxAmount</c> already uses.
        /// </summary>
        Task<ulong> GetMaxAmountPerTransactionAsync(string email);

        /// <summary>Sets (or clears, with <c>0</c>) the caller's per-transaction spending limit.</summary>
        Task SetMaxAmountPerTransactionAsync(string email, ulong maxAmountPerTransaction);
    }
}
