namespace BiatecMCP.Model
{
    /// <summary>
    /// Request body for BiatecOIDC's <c>POST /wallet/sign/{network}/{address}</c> - mirrored here (rather
    /// than referencing BiatecOIDC's own model type) so this project has no compile-time dependency on
    /// BiatecOIDC; the two services are independently deployed and only ever talk to each other over HTTP.
    /// Which identity signs is the <c>address</c> route segment now, not a body field - see
    /// <see cref="IBiatecWalletClient.SignAsync"/>.
    /// </summary>
    public class SignTransactionGroupRequest
    {
        /// <summary>One or more unsigned transactions, each base64-encoded canonical msgpack.</summary>
        public List<string> Transactions { get; set; } = new();
    }

    /// <summary>Response body for <c>POST /wallet/sign</c>.</summary>
    public class SignTransactionGroupResponse
    {
        /// <summary>The signed transactions, base64-encoded msgpack, in the same order as the request.</summary>
        public List<string> SignedTransactions { get; set; } = new();
    }

    /// <summary>One seed in the caller's vault, as returned by <c>GET /wallet/seeds</c>. Never includes the mnemonic.</summary>
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

    /// <summary>
    /// Response body for <c>GET /wallet/address/{seedAddress}/{slot?}</c> - the derived address for every
    /// currently-supported chain family in one call (there's no per-EVM-chain concept at this layer, and
    /// AVM is genesis-independent).
    /// </summary>
    public class DerivedAddressResponse
    {
        /// <summary>The derived Algorand-family (AVM) address.</summary>
        public string Address { get; set; } = string.Empty;

        /// <summary>The derived Ethereum-family (EVM) address, <c>"0x..."</c>.</summary>
        public string EvmAddress { get; set; } = string.Empty;

        /// <summary>The derived Bitcoin mainnet P2WPKH (native SegWit, <c>bc1...</c>) address.</summary>
        public string BitcoinAddress { get; set; } = string.Empty;

        /// <summary>The derived Bitcoin Cash mainnet CashAddr (<c>bitcoincash:q...</c>) address.</summary>
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

        /// <summary>Whether BiatecOIDC currently knows which key signs for <see cref="Address"/>.</summary>
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

        /// <summary>When this pairing became active.</summary>
        public DateTimeOffset ActivatedUtc { get; set; }
    }

    /// <summary>Response body for <c>GET /wallet/active-addresses</c>.</summary>
    public class ListActiveAddressesResponse
    {
        /// <summary>Every address currently resolvable to a signing seed/slot.</summary>
        public List<ActiveAddressResponse> Addresses { get; set; } = new();
    }
}
