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

    /// <summary>One seed's identifying address, as returned by <c>GET /wallet/address</c>.</summary>
    public class AddressResponse
    {
        /// <summary>This seed's identifying (ARC-76 slot-0) address.</summary>
        public string Address { get; set; } = string.Empty;

        /// <summary>Whether this is the seed currently used for normal signing when no <c>PrimaryAddress</c> is given.</summary>
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
        /// <summary>The derived ARC-76 address.</summary>
        public string Address { get; set; } = string.Empty;

        /// <summary>The seed's identifying (slot-0) address, echoed back.</summary>
        public string PrimaryAddress { get; set; } = string.Empty;

        /// <summary>The ARC-76 derivation slot that was used, echoed back.</summary>
        public int Slot { get; set; }
    }

    /// <summary>One seed's EVM address, as returned by <c>GET /wallet/evm/address</c>. Same across every EVM chain (Ethereum, Gnosis, Arbitrum, Base, ...) - there's no per-chain concept at this layer.</summary>
    public class EvmAddressResponse
    {
        /// <summary>This seed's EVM address (slot 0), <c>"0x..."</c>.</summary>
        public string Address { get; set; } = string.Empty;

        /// <summary>Whether this is the seed currently used for normal signing when no <c>PrimaryAddress</c> is given.</summary>
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
        /// <summary>The derived EVM address.</summary>
        public string Address { get; set; } = string.Empty;

        /// <summary>The seed's identifying (Algorand slot-0) address, echoed back.</summary>
        public string PrimaryAddress { get; set; } = string.Empty;

        /// <summary>The ARC-76 derivation slot that was used, echoed back.</summary>
        public int Slot { get; set; }
    }

    /// <summary>Request body for <c>POST /wallet/{network}/{address}/activate</c>.</summary>
    public class ActivateAddressRequest
    {
        /// <summary>Which seed's key signs for the address being activated - its own identifying (Algorand slot-0) address.</summary>
        public string PrimaryAddress { get; set; } = string.Empty;

        /// <summary>ARC-76 derivation slot within that seed. Defaults to <c>0</c>.</summary>
        public int Slot { get; set; }
    }

    /// <summary>
    /// Response body for <c>GET /wallet/{network}/{address}/info</c> and
    /// <c>POST /wallet/{network}/{address}/activate</c>.
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
        public string? PrimaryAddress { get; set; }

        /// <summary>ARC-76 slot of <see cref="PrimaryAddress"/> - meaningless if <see cref="IsActive"/> is <c>false</c>.</summary>
        public int Slot { get; set; }
    }
}
