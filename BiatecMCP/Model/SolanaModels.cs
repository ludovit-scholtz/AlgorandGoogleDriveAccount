namespace BiatecMCP.Model
{
    /// <summary>
    /// Wire shape of an unsigned native-SOL transfer: <c>createSolanaTransaction</c> serializes this (as UTF-8 JSON,
    /// base64-encoded) and BiatecOIDC's <c>POST /wallet/solana/{address}/sign</c> deserializes its own identical
    /// <c>SolanaUnsignedTransfer</c> (duplicated rather than shared, per this repo's no-compile-time-coupling rule -
    /// property names must stay in sync). BiatecOIDC builds the actual Solana message from these three fields itself.
    /// </summary>
    public class SolanaUnsignedTransfer
    {
        public string Recipient { get; set; } = string.Empty;

        public ulong Lamports { get; set; }

        public string RecentBlockhash { get; set; } = string.Empty;
    }
}
