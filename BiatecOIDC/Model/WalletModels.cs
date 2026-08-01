namespace BiatecOIDC.Model
{
    /// <summary>Request body for <c>POST /wallet/sign</c>.</summary>
    public class SignTransactionGroupRequest
    {
        /// <summary>
        /// One or more transactions, each base64-encoded msgpack - a bare unsigned <c>Transaction</c>, or a
        /// <c>SignedTransaction</c> wrapper for a multisig co-signing scenario. Multiple entries are signed
        /// as an atomic group (the caller is responsible for having already computed and assigned the
        /// group id across them before calling this endpoint).
        /// </summary>
        public List<string> Transactions { get; set; } = new();
    }

    /// <summary>Response body for <c>POST /wallet/sign</c>.</summary>
    public class SignTransactionGroupResponse
    {
        /// <summary>The signed transactions, base64-encoded msgpack, in the same order as the request.</summary>
        public List<string> SignedTransactions { get; set; } = new();
    }

    /// <summary>Request body for <c>PUT /wallet/limits</c>.</summary>
    public class UpdateSpendingLimitRequest
    {
        /// <summary>
        /// ISO 4217 currency code the three limits below are expressed in (e.g. <c>"USD"</c>, <c>"EUR"</c>,
        /// <c>"CZK"</c>) - see <c>GET /wallet/limits/currencies</c> for the full supported list. Defaults
        /// to <c>"USD"</c> if left blank.
        /// </summary>
        public string CurrencyCode { get; set; } = "USD";

        /// <summary>Maximum total spend allowed in the trailing 24 hours, in <see cref="CurrencyCode"/>. <c>0</c> means unbounded.</summary>
        public decimal DailyLimit { get; set; }

        /// <summary>Maximum total spend allowed in the trailing 7 days, in <see cref="CurrencyCode"/>. <c>0</c> means unbounded.</summary>
        public decimal WeeklyLimit { get; set; }

        /// <summary>Maximum total spend allowed in the trailing 30 days, in <see cref="CurrencyCode"/>. <c>0</c> means unbounded.</summary>
        public decimal MonthlyLimit { get; set; }
    }

    /// <summary>Response body for <c>GET /wallet/limits</c> and <c>PUT /wallet/limits</c>.</summary>
    public class SpendingLimitResponse
    {
        /// <summary>ISO 4217 currency code the three limits below are expressed in.</summary>
        public string CurrencyCode { get; set; } = "USD";

        /// <summary>The caller's current daily (trailing 24h) spending limit, in <see cref="CurrencyCode"/>. <c>0</c> means unbounded.</summary>
        public decimal DailyLimit { get; set; }

        /// <summary>The caller's current weekly (trailing 7d) spending limit, in <see cref="CurrencyCode"/>. <c>0</c> means unbounded.</summary>
        public decimal WeeklyLimit { get; set; }

        /// <summary>The caller's current monthly (trailing 30d) spending limit, in <see cref="CurrencyCode"/>. <c>0</c> means unbounded.</summary>
        public decimal MonthlyLimit { get; set; }
    }

    /// <summary>One supported currency and its current rate, as returned by <c>GET /wallet/limits/currencies</c>.</summary>
    public class CurrencyRateResponse
    {
        /// <summary>ISO 4217 currency code (e.g. <c>"EUR"</c>).</summary>
        public string Code { get; set; } = string.Empty;

        /// <summary>Human-readable name, when known (e.g. <c>"European Monetary Union euro"</c>).</summary>
        public string? Name { get; set; }

        /// <summary>How many USD 1 unit of <see cref="Code"/> is currently worth.</summary>
        public decimal UsdPerUnit { get; set; }
    }

    /// <summary>Response body for <c>GET /wallet/limits/currencies</c>.</summary>
    public class SupportedCurrenciesResponse
    {
        /// <summary>
        /// Every currency a spending limit can be configured in, sorted by code. Rates come from the Czech
        /// National Bank's daily fixing and are cached - not a real-time feed (see
        /// <c>ExchangeRateConfiguration.CacheDurationMinutes</c>).
        /// </summary>
        public List<CurrencyRateResponse> Currencies { get; set; } = new();
    }

    /// <summary>One seed in the caller's vault, as returned by <c>GET /wallet/seeds</c> and <c>POST /wallet/seeds</c>. Never includes the mnemonic.</summary>
    public class SeedResponse
    {
        /// <summary>This seed's identifying address - its ARC-76 slot-0 derived account address.</summary>
        public string Address { get; set; } = string.Empty;

        /// <summary>When this seed was generated.</summary>
        public DateTimeOffset CreatedUtc { get; set; }

        /// <summary>Whether this is the seed currently used for normal signing (<c>POST /wallet/sign</c>).</summary>
        public bool IsPrimary { get; set; }
    }

    /// <summary>Response body for <c>GET /wallet/seeds</c>.</summary>
    public class ListSeedsResponse
    {
        /// <summary>Every seed ever generated for this user, oldest first. Exactly one has <see cref="SeedResponse.IsPrimary"/> set.</summary>
        public List<SeedResponse> Seeds { get; set; } = new();
    }

    /// <summary>Request body for <c>PUT /wallet/seeds/primary</c>.</summary>
    public class SwitchPrimarySeedRequest
    {
        /// <summary>The identifying address (see <see cref="SeedResponse.Address"/>) of the seed to make primary.</summary>
        public string Address { get; set; } = string.Empty;
    }
}
