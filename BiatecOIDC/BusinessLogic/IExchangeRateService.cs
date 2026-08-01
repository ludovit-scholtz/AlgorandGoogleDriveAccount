namespace BiatecOIDC.BusinessLogic
{
    /// <summary>
    /// Currency exchange rates for spending-limit configuration, sourced from the Czech National Bank's
    /// daily fixing (see <see cref="Model.ExchangeRateConfiguration"/>) and cached - <em>not</em> a
    /// real-time feed. USD is always supported (rate <c>1.0</c>, never requires a fetch); every other
    /// supported currency comes from the ČNB table, plus CZK itself (derived, since ČNB never quotes CZK
    /// against CZK).
    /// </summary>
    public interface IExchangeRateService
    {
        /// <summary>
        /// Every currency a spending limit can be configured in, each with its current USD rate. Backs
        /// <c>GET /wallet/limits/currencies</c>.
        /// </summary>
        Task<IReadOnlyList<CurrencyRate>> GetSupportedCurrenciesAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Converts a USD amount into <paramref name="currencyCode"/> using the current cached rate.
        /// </summary>
        /// <exception cref="BusinessLogic.UnsupportedCurrencyException"><paramref name="currencyCode"/> isn't supported.</exception>
        Task<decimal> ConvertFromUsdAsync(decimal amountUsd, string currencyCode, CancellationToken cancellationToken = default);

        /// <summary>Whether <paramref name="currencyCode"/> is one a spending limit can be set in.</summary>
        Task<bool> IsSupportedCurrencyAsync(string? currencyCode, CancellationToken cancellationToken = default);
    }
}
