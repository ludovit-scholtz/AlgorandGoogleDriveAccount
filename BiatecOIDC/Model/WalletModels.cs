namespace BiatecOIDC.Model
{
    /// <summary>
    /// Request body for <c>POST /wallet/{network}/{address}/sign</c> - <c>address</c> (route segment) is
    /// which identity signs; there is no <c>SeedAddress</c>/<c>Slot</c> selector here anymore (see
    /// <c>WalletController.SignTransactionGroup</c>'s remarks for how <c>address</c> resolves to one).
    /// Each <see cref="Transactions"/> entry's encoding depends on <c>network</c>'s chain family: base64
    /// msgpack for AVM, base64 UTF-8 JSON (<see cref="EvmTransactionRequest"/>) for EVM.
    /// </summary>
    public class SignTransactionGroupRequest
    {
        /// <summary>
        /// One or more transactions, each base64-encoded - a bare unsigned Algorand <c>Transaction</c>
        /// msgpack, an Algorand <c>SignedTransaction</c> msgpack wrapper for a multisig co-signing scenario,
        /// or (for an EVM <c>network</c>) UTF-8 JSON matching <see cref="EvmTransactionRequest"/>. Multiple
        /// entries are signed as an atomic group (the caller is responsible for having already computed and
        /// assigned the group id across them before calling this endpoint - EVM has no equivalent grouping
        /// concept, so each EVM entry is signed independently).
        /// </summary>
        public List<string> Transactions { get; set; } = new();
    }

    /// <summary>
    /// The JSON shape (base64-encoded, one per <see cref="SignTransactionGroupRequest.Transactions"/> entry)
    /// an unsigned EVM transaction is submitted as. Numeric fields are decimal or <c>0x</c>-prefixed hex
    /// strings (never JSON numbers - wei-scale values routinely exceed <c>double</c>'s safe integer range).
    /// Set <see cref="GasPrice"/> for a legacy (EIP-155) transaction, or both <see cref="MaxFeePerGas"/> and
    /// <see cref="MaxPriorityFeePerGas"/> for an EIP-1559 one - not both fee shapes at once.
    /// </summary>
    public class EvmTransactionRequest
    {
        /// <summary>The destination chain's id (EIP-155) - see <c>GET /chains</c>/<c>listSupportedNetworks</c>.</summary>
        public string ChainId { get; set; } = string.Empty;

        /// <summary>The sending account's transaction count (nonce).</summary>
        public string Nonce { get; set; } = string.Empty;

        /// <summary>Recipient address, <c>"0x..."</c>. Empty for a contract-creation transaction.</summary>
        public string To { get; set; } = string.Empty;

        /// <summary>Amount to transfer, in wei. Defaults to <c>0</c>.</summary>
        public string Value { get; set; } = "0";

        /// <summary>Call data / contract-creation bytecode, hex-encoded (<c>"0x..."</c>). Defaults to empty.</summary>
        public string Data { get; set; } = string.Empty;

        /// <summary>Maximum gas this transaction may consume.</summary>
        public string GasLimit { get; set; } = string.Empty;

        /// <summary>Legacy (pre-EIP-1559) gas price, in wei.</summary>
        public string? GasPrice { get; set; }

        /// <summary>EIP-1559 max total fee per gas, in wei.</summary>
        public string? MaxFeePerGas { get; set; }

        /// <summary>EIP-1559 max priority fee (tip) per gas, in wei.</summary>
        public string? MaxPriorityFeePerGas { get; set; }
    }

    /// <summary>Response body for <c>POST /wallet/sign</c>.</summary>
    public class SignTransactionGroupResponse
    {
        /// <summary>The signed transactions, base64-encoded msgpack, in the same order as the request.</summary>
        public List<string> SignedTransactions { get; set; } = new();

        /// <summary>
        /// Non-fatal warnings about this signed group - currently populated only when it contains one or more
        /// Algorand transactions of a type <c>POST /wallet/{network}/{address}/sign</c> does not price against
        /// the spending limit (application calls, asset configuration, and any other non-payment/non-asset-
        /// transfer type - <c>appl</c> in particular can move arbitrary value via inner transactions, which
        /// this endpoint cannot see or price). Empty for a group with nothing to warn about. Added so a
        /// signed group's spend history is never silently understated (audit finding M-03/R-028).
        /// </summary>
        public List<string> Warnings { get; set; } = new();
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

    /// <summary>
    /// Response body for <c>GET</c>/<c>PUT /wallet/limits</c> (the global bucket) and
    /// <c>GET</c>/<c>PUT /wallet/{network}/{address}/limits</c> (a per-address bucket).
    /// </summary>
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

        /// <summary>The queried address (route segment), echoed back - <c>null</c> for the account-wide global bucket.</summary>
        public string? Address { get; set; }

        /// <summary>The queried network (route segment), echoed back - <c>null</c> for the account-wide global bucket.</summary>
        public string? Network { get; set; }

        /// <summary>Which seed's bucket this is - resolved from <see cref="Address"/>. <c>null</c> for the global bucket.</summary>
        public string? SeedAddress { get; set; }

        /// <summary>ARC-76 slot of <see cref="SeedAddress"/>'s bucket - meaningless when <see cref="SeedAddress"/> is <c>null</c>.</summary>
        public int Slot { get; set; }

        /// <summary>
        /// Whether this limit is actually enforced by <c>POST /wallet/{network}/{address}/sign</c> on
        /// <see cref="Network"/> today. <c>true</c> for Bitcoin/Bitcoin Cash and Algorand mainnet; <c>false</c>
        /// for every EVM chain (native-currency valuation isn't implemented yet) and every non-mainnet AVM
        /// chain (the Biatec Router that prices assets isn't deployed there). <c>null</c> for the account-wide
        /// global bucket (<see cref="Network"/> is also <c>null</c> there), since enforcement is a per-network
        /// question - see <c>GET /wallet/{network}/{address}/limits</c> for a network-specific answer. Added
        /// so a configured limit is never silently weaker than the API's own response implies (audit findings
        /// M-01/R-026, M-02/R-027).
        /// </summary>
        public bool? LimitsEnforced { get; set; }
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

    /// <summary>
    /// Response body for <c>GET /wallet/address/{seedAddress}/{slot?}</c> - the derived address for every
    /// currently-supported chain family (there is no per-EVM-chain concept at this layer, and AVM is
    /// genesis-independent - see <c>CLAUDE.md</c>'s "EVM (Ethereum-family) support" note), rather than a
    /// single-family derive endpoint per family.
    /// </summary>
    public class DerivedAddressResponse
    {
        /// <summary>The derived Algorand-family (AVM) address at <see cref="Slot"/> for the seed identified by <see cref="SeedAddress"/>.</summary>
        public string Address { get; set; } = string.Empty;

        /// <summary>The derived Ethereum-family (EVM) address at <see cref="Slot"/> for the same seed, <c>"0x..."</c>.</summary>
        public string EvmAddress { get; set; } = string.Empty;

        /// <summary>The derived Bitcoin mainnet P2WPKH (native SegWit, <c>bc1...</c>) address for the same seed/slot.</summary>
        public string BitcoinAddress { get; set; } = string.Empty;

        /// <summary>The derived Bitcoin Cash mainnet CashAddr (<c>bitcoincash:q...</c>) address for the same seed/slot.</summary>
        public string BitcoinCashAddress { get; set; } = string.Empty;

        /// <summary>The seed's identifying (Algorand slot-0) address, echoed back.</summary>
        public string SeedAddress { get; set; } = string.Empty;

        /// <summary>The ARC-76 derivation slot that was used, echoed back.</summary>
        public int Slot { get; set; }
    }

    /// <summary>Request body for <c>POST /wallet/{network}/{seedAddress}/{slot}/activate</c>.</summary>
    public class ActivateAddressRequest
    {
        /// <summary>The address to register as signed by <c>{seedAddress}</c>/<c>{slot}</c> (the route segments).</summary>
        public string Address { get; set; } = string.Empty;
    }

    /// <summary>
    /// Response body for <c>GET /wallet/{network}/{address}/info</c> and
    /// <c>POST /wallet/{network}/{seedAddress}/{slot}/activate</c>.
    /// </summary>
    public class AddressInfoResponse
    {
        /// <summary>The queried address, echoed back.</summary>
        public string Address { get; set; } = string.Empty;

        /// <summary>The queried network, echoed back.</summary>
        public string Network { get; set; } = string.Empty;

        /// <summary><c>"Avm"</c> or <c>"Evm"</c>.</summary>
        public string Family { get; set; } = string.Empty;

        /// <summary>
        /// Whether Biatec currently knows which key signs for <see cref="Address"/> - either it's a seed's
        /// own primary address (implicit), it was derived at least once via
        /// <c>GET /wallet/address/{seedAddress}/{slot}</c> (or its EVM counterpart), or it was explicitly
        /// activated via <c>POST /wallet/{network}/{seedAddress}/{slot}/activate</c> after an on-chain rekey check.
        /// </summary>
        public bool IsActive { get; set; }

        /// <summary>Which seed signs for <see cref="Address"/> - <c>null</c> if <see cref="IsActive"/> is <c>false</c>.</summary>
        public string? SeedAddress { get; set; }

        /// <summary>ARC-76 slot of <see cref="SeedAddress"/> - meaningless if <see cref="IsActive"/> is <c>false</c>.</summary>
        public int Slot { get; set; }
    }

    /// <summary>One address in the caller's active-address mapping, as returned by <c>GET /wallet/active-addresses</c>.</summary>
    public class ActiveAddressResponse
    {
        /// <summary>The active address itself.</summary>
        public string Address { get; set; } = string.Empty;

        /// <summary><c>"Avm"</c> or <c>"Evm"</c>.</summary>
        public string Family { get; set; } = string.Empty;

        /// <summary>Which seed's key signs for <see cref="Address"/>.</summary>
        public string SeedAddress { get; set; } = string.Empty;

        /// <summary>ARC-76 derivation slot within that seed.</summary>
        public int Slot { get; set; }

        /// <summary>
        /// When this pairing became active - a seed's own slot-0 AVM address (active implicitly, never
        /// requiring a derive/activate call) reports the seed's own <c>CreatedUtc</c>.
        /// </summary>
        public DateTimeOffset ActivatedUtc { get; set; }
    }

    /// <summary>Response body for <c>GET /wallet/active-addresses</c>.</summary>
    public class ListActiveAddressesResponse
    {
        /// <summary>
        /// Every address currently resolvable to a signing seed/slot - every seed's own slot-0 AVM address
        /// (active implicitly) plus every explicitly-activated entry (any non-zero AVM slot, every EVM
        /// address, and any externally-rekeyed AVM address) from the address activation registry.
        /// </summary>
        public List<ActiveAddressResponse> Addresses { get; set; } = new();
    }
}
