namespace BiatecMCP.Model
{
    /// <summary>
    /// Request body for BiatecOIDC's <c>POST /wallet/sign</c> - mirrored here (rather than referencing
    /// BiatecOIDC's own model type) so this project has no compile-time dependency on BiatecOIDC; the two
    /// services are independently deployed and only ever talk to each other over HTTP.
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
}
