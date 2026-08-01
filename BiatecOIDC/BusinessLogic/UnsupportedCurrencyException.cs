namespace BiatecOIDC.BusinessLogic
{
    /// <summary>
    /// Thrown by <see cref="IExchangeRateService"/> when asked to convert to/validate a currency code that
    /// isn't in the supported list (see <c>GET /wallet/limits/currencies</c>) - either never published by
    /// the Czech National Bank's daily fixing, or not recognized (e.g. a typo). Mapped to 400 by
    /// <c>WalletController</c> when setting a spending limit.
    /// </summary>
    public sealed class UnsupportedCurrencyException : Exception
    {
        /// <summary>The rejected currency code, as supplied by the caller.</summary>
        public string CurrencyCode { get; }

        public UnsupportedCurrencyException(string currencyCode)
            : base($"'{currencyCode}' is not a supported spending-limit currency. See GET /wallet/limits/currencies for the supported list.")
        {
            CurrencyCode = currencyCode;
        }
    }
}
