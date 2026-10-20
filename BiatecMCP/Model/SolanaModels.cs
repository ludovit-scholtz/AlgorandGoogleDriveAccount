using System.Text.Json.Serialization;

namespace BiatecMCP.Model
{
    /// <summary>
    /// Wire shape of an unsigned native-SOL transfer: <c>createSolanaTransaction</c> serializes this (as UTF-8 JSON,
    /// base64-encoded) and BiatecOIDC's <c>POST /wallet/solana/{address}/sign</c> deserializes its own identical
    /// <c>SolanaUnsignedTransfer</c> (duplicated rather than shared, per this repo's no-compile-time-coupling rule -
    /// property names are pinned with <c>[JsonPropertyName]</c> on both sides and covered by a contract test). BiatecOIDC builds the actual Solana message from these three fields itself.
    /// </summary>
    public class SolanaUnsignedTransfer
    {
        [JsonPropertyName("Recipient")]
        public string Recipient { get; set; } = string.Empty;

        [JsonPropertyName("Lamports")]
        public ulong Lamports { get; set; }

        [JsonPropertyName("RecentBlockhash")]
        public string RecentBlockhash { get; set; } = string.Empty;
    }
}
