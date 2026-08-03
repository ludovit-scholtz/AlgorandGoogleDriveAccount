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

        /// <summary>
        /// Which seed signs this group - its own identifying (slot-0) address, see <c>GET /wallet/address</c>.
        /// Omitted (the default) signs with the vault's current primary seed, unchanged from before this
        /// field existed.
        /// </summary>
        public string? PrimaryAddress { get; set; }

        /// <summary>ARC-76 derivation slot within the selected seed. Defaults to <c>0</c>.</summary>
        public int Slot { get; set; }
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

        /// <summary>Which bucket this response describes - <c>null</c> means the account-wide global bucket.</summary>
        public string? PrimaryAddress { get; set; }

        /// <summary>ARC-76 slot of <see cref="PrimaryAddress"/>'s bucket - meaningless when <see cref="PrimaryAddress"/> is <c>null</c>.</summary>
        public int Slot { get; set; }
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

    /// <summary>One seed's identifying address, as returned by <c>GET /wallet/address</c>.</summary>
    public class AddressResponse
    {
        /// <summary>This seed's identifying (ARC-76 slot-0) address.</summary>
        public string Address { get; set; } = string.Empty;

        /// <summary>Whether this is the seed currently used for normal signing (<c>POST /wallet/sign</c>) when no <c>PrimaryAddress</c> is given.</summary>
        public bool IsPrimary { get; set; }
    }

    /// <summary>Response body for <c>GET /wallet/address</c>.</summary>
    public class ListAddressesResponse
    {
        /// <summary>Every seed's identifying address in the caller's vault. Exactly one has <see cref="AddressResponse.IsPrimary"/> set.</summary>
        public List<AddressResponse> Addresses { get; set; } = new();
    }

    /// <summary>Response body for <c>GET /wallet/address/{primaryAddress}/{slot?}</c>.</summary>
    public class DerivedAddressResponse
    {
        /// <summary>The derived ARC-76 address at <see cref="Slot"/> for the seed identified by <see cref="PrimaryAddress"/>.</summary>
        public string Address { get; set; } = string.Empty;

        /// <summary>The seed's identifying (slot-0) address, echoed back.</summary>
        public string PrimaryAddress { get; set; } = string.Empty;

        /// <summary>The ARC-76 derivation slot that was used, echoed back.</summary>
        public int Slot { get; set; }
    }

    /// <summary>One seed's EVM address, as returned by <c>GET /wallet/evm/address</c>. Same seed as <see cref="AddressResponse"/> - just derived via <c>ARC76.GetEVMEmailAccount</c> instead of <c>ARC76.GetEmailAccount</c>, so it's the same address across every EVM chain.</summary>
    public class EvmAddressResponse
    {
        /// <summary>This seed's EVM address (slot 0), <c>"0x..."</c>.</summary>
        public string Address { get; set; } = string.Empty;

        /// <summary>Whether this is the seed currently used for normal signing (<c>POST /wallet/sign</c>) when no <c>PrimaryAddress</c> is given.</summary>
        public bool IsPrimary { get; set; }
    }

    /// <summary>Response body for <c>GET /wallet/evm/address</c>.</summary>
    public class ListEvmAddressesResponse
    {
        /// <summary>Every seed's EVM address in the caller's vault. Exactly one has <see cref="EvmAddressResponse.IsPrimary"/> set.</summary>
        public List<EvmAddressResponse> Addresses { get; set; } = new();
    }

    /// <summary>Response body for <c>GET /wallet/evm/address/{primaryAddress}/{slot?}</c>.</summary>
    public class DerivedEvmAddressResponse
    {
        /// <summary>The derived EVM address at <see cref="Slot"/> for the seed identified by <see cref="PrimaryAddress"/>.</summary>
        public string Address { get; set; } = string.Empty;

        /// <summary>The seed's identifying (Algorand slot-0) address, echoed back.</summary>
        public string PrimaryAddress { get; set; } = string.Empty;

        /// <summary>The ARC-76 derivation slot that was used, echoed back.</summary>
        public int Slot { get; set; }
    }
}
