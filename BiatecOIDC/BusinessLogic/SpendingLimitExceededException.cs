namespace BiatecOIDC.BusinessLogic
{
    /// <summary>
    /// Thrown by <see cref="ISpendingLimitService"/> when signing a transaction group would push the
    /// caller's rolling daily, weekly, or monthly spend - converted into their configured limit currency -
    /// over a configured (non-zero) ceiling. Caught by <c>WalletController</c> and mapped to 403 - an
    /// expected, caller-correctable outcome, never a 500.
    /// </summary>
    public sealed class SpendingLimitExceededException : Exception
    {
        /// <summary>Which rolling window was exceeded: <c>"daily"</c> (24h), <c>"weekly"</c> (7d), or <c>"monthly"</c> (30d).</summary>
        public string Window { get; }

        /// <summary>The total spend that would result (already-recorded spend in the window, plus this group), in <see cref="CurrencyCode"/>.</summary>
        public decimal ProjectedAmount { get; }

        /// <summary>The configured ceiling for <see cref="Window"/>, in <see cref="CurrencyCode"/>.</summary>
        public decimal Limit { get; }

        /// <summary>ISO 4217 currency code both <see cref="ProjectedAmount"/> and <see cref="Limit"/> are expressed in.</summary>
        public string CurrencyCode { get; }

        public SpendingLimitExceededException(string window, decimal projectedAmount, decimal limit, string currencyCode)
            : base($"Signing this would bring your {window} spend to {projectedAmount:0.####} {currencyCode}, exceeding your configured {window} limit of {limit:0.####} {currencyCode}.")
        {
            Window = window;
            ProjectedAmount = projectedAmount;
            Limit = limit;
            CurrencyCode = currencyCode;
        }
    }
}
