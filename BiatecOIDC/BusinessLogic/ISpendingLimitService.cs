namespace BiatecOIDC.BusinessLogic
{
    /// <summary>
    /// A wallet owner's daily/weekly/monthly spending limits, enforced by <see cref="IWalletService"/> on
    /// every <c>POST /wallet/sign</c> call. Everything is stored AES-encrypted in the owner's own cloud
    /// drive (Google Drive/OneDrive, whichever they signed in with) - the settings file
    /// (<see cref="SpendingLimitSettings"/>) and a rolling ledger of every signed payment/asset-transfer
    /// (<see cref="SpendingLedgerEntry"/>), exactly like the self-custody account file itself. Biatec's
    /// servers never see this data in plaintext and never persist it themselves.
    /// </summary>
    /// <remarks>
    /// The three windows are trailing/rolling, not calendar-aligned: "daily" is the last 24 hours, "weekly"
    /// the last 7 days, "monthly" the last 30 days - all measured back from "now", not from midnight or the
    /// 1st of the month. This is deliberate: a calendar-aligned window resets its budget the instant a
    /// calendar boundary passes, which would let a user spend up to 2x a "daily" limit in the hour either
    /// side of midnight. A rolling window has no such reset point.
    /// </remarks>
    public interface ISpendingLimitService
    {
        /// <summary>
        /// The caller's currently configured limits. Returns an all-zero (fully unbounded), USD-denominated
        /// default if the owner has never set any - so a first-time caller always gets a well-formed
        /// response rather than a 404.
        /// </summary>
        Task<SpendingLimitSettings> GetLimitsAsync(string email, string provider, string? accessToken, CancellationToken cancellationToken = default);

        /// <summary>
        /// Persists new limits. <paramref name="settings"/>'s <see cref="SpendingLimitSettings.CurrencyCode"/>
        /// is validated against <c>IExchangeRateService</c>'s supported list before writing.
        /// </summary>
        /// <exception cref="UnsupportedCurrencyException">The requested currency isn't supported.</exception>
        Task SetLimitsAsync(string email, string provider, string? accessToken, SpendingLimitSettings settings, CancellationToken cancellationToken = default);

        /// <summary>
        /// Checks whether signing a group worth <paramref name="amountUsd"/> (as priced by
        /// <c>IAssetValuationService</c>) would exceed any configured (non-zero) daily/weekly/monthly
        /// ceiling, based on the ledger's recorded spend in each window so far. Does <em>not</em> record
        /// the spend itself - call <see cref="RecordSpendAsync"/> once signing has actually succeeded.
        /// </summary>
        /// <exception cref="SpendingLimitExceededException">A configured window would be exceeded.</exception>
        Task EnsureWithinLimitsAsync(string email, string provider, string? accessToken, decimal amountUsd, CancellationToken cancellationToken = default);

        /// <summary>
        /// Appends <paramref name="entries"/> (one per signed payment/asset-transfer) to the caller's
        /// encrypted ledger, then prunes entries older than the longest window (30 days) so the file stays
        /// bounded in size.
        /// </summary>
        Task RecordSpendAsync(string email, string provider, string? accessToken, IReadOnlyList<SpendingLedgerEntry> entries, CancellationToken cancellationToken = default);
    }
}
