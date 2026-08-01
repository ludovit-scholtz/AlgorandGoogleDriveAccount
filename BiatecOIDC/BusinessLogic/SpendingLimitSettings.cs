namespace BiatecOIDC.BusinessLogic
{
    /// <summary>
    /// A wallet owner's configured daily/weekly/monthly spending ceilings and the currency they're
    /// expressed in. This is the exact shape persisted, AES-encrypted, in the owner's own cloud drive by
    /// <see cref="ISpendingLimitService"/> - never on Biatec's servers.
    /// </summary>
    public sealed class SpendingLimitSettings
    {
        /// <summary>
        /// ISO 4217 currency code the three limits below are expressed in (see
        /// <c>GET /wallet/limits/currencies</c> for the supported list). Defaults to <c>"USD"</c> - every
        /// wallet starts out limited in USD until the owner explicitly picks a different currency.
        /// </summary>
        public string CurrencyCode { get; set; } = "USD";

        /// <summary>Maximum total spend allowed in the trailing 24 hours, in <see cref="CurrencyCode"/>. <c>0</c> = unbounded.</summary>
        public decimal DailyLimit { get; set; }

        /// <summary>Maximum total spend allowed in the trailing 7 days, in <see cref="CurrencyCode"/>. <c>0</c> = unbounded.</summary>
        public decimal WeeklyLimit { get; set; }

        /// <summary>Maximum total spend allowed in the trailing 30 days, in <see cref="CurrencyCode"/>. <c>0</c> = unbounded.</summary>
        public decimal MonthlyLimit { get; set; }
    }
}
