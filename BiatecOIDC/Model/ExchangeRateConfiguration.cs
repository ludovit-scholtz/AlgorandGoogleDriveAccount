namespace BiatecOIDC.Model
{
    /// <summary>
    /// Bound from the <c>ExchangeRates</c> configuration section. Controls where
    /// <c>BusinessLogic.IExchangeRateService</c> fetches currency exchange rates from and how long it
    /// caches them.
    /// </summary>
    public class ExchangeRateConfiguration
    {
        /// <summary>
        /// The Czech National Bank's public daily exchange-rate-fixing JSON API - documented at
        /// https://api.cnb.cz/cnbapi/swagger-ui.html. Requires no API key. Rates are CZK per unit of
        /// foreign currency, updated once per Czech business day (~14:30 CET).
        /// </summary>
        public string CnbDailyRatesUrl { get; set; } = "https://api.cnb.cz/cnbapi/exrates/daily?lang=EN";

        /// <summary>
        /// How long a fetched rate table is cached before being re-fetched. Defaults to 6 hours - the ČNB
        /// fixing only changes once a day, so this just bounds how stale a rate can be after a fresh
        /// publish, while keeping normal traffic from ever hitting the upstream API.
        /// </summary>
        public int CacheDurationMinutes { get; set; } = 360;
    }
}
