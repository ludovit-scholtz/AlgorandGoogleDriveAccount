using System.Text.Json.Serialization;

namespace BiatecOIDC.BusinessLogic
{
    /// <summary>Deserialization shape of the Czech National Bank's daily exchange-rate-fixing JSON API.</summary>
    internal sealed class CnbDailyRatesResponse
    {
        [JsonPropertyName("rates")]
        public List<CnbRate> Rates { get; set; } = new();
    }

    /// <summary>
    /// One currency's fixing for the day. <see cref="Rate"/> is CZK per <see cref="Amount"/> units of
    /// <see cref="CurrencyCode"/> - some currencies (e.g. JPY, HUF, IDR) are quoted per 100 or per 1000
    /// units rather than per 1, hence <see cref="Amount"/> is not always <c>1</c>.
    /// </summary>
    internal sealed class CnbRate
    {
        [JsonPropertyName("country")]
        public string? Country { get; set; }

        [JsonPropertyName("currency")]
        public string? Currency { get; set; }

        [JsonPropertyName("amount")]
        public int Amount { get; set; }

        [JsonPropertyName("currencyCode")]
        public string CurrencyCode { get; set; } = string.Empty;

        [JsonPropertyName("rate")]
        public decimal Rate { get; set; }
    }
}
