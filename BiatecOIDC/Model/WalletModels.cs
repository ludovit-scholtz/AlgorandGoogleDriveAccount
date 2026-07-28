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
        /// The caller's current Google/Microsoft access token for the signed-in provider, used to
        /// read/decrypt the self-custody account file. Required unless the caller is relying on an
        /// ambient cookie session (not applicable for server-to-server bearer-token calls).
        /// </summary>
        public string? AccessToken { get; set; }
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
        /// The new maximum amount (microAlgos for a payment, base units for an asset transfer) allowed in
        /// a single transaction signed via <c>/wallet/sign</c>. <c>0</c> means unbounded.
        /// </summary>
        public ulong MaxAmountPerTransaction { get; set; }
    }

    /// <summary>Response body for <c>GET /wallet/limits</c> and <c>PUT /wallet/limits</c>.</summary>
    public class SpendingLimitResponse
    {
        /// <summary>The caller's current per-transaction spending limit. <c>0</c> means unbounded.</summary>
        public ulong MaxAmountPerTransaction { get; set; }
    }
}
