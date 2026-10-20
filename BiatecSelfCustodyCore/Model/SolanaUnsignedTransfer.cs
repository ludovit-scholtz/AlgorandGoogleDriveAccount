using System.Text.Json.Serialization;

namespace BiatecSelfCustodyCore.Model
{
    /// <summary>
    /// An unsigned native-SOL transfer - structured fields rather than a pre-serialized Solana message,
    /// mirroring <see cref="EvmUnsignedTransaction"/>/<see cref="BitcoinUnsignedTransaction"/>'s reasoning, and for a
    /// stronger reason than there: a Solana transaction can carry any number of arbitrary program instructions,
    /// so a pre-serialized blob could hide spend (or an authority change) that nothing here could price or
    /// limit. <c>DriveService.SignSolanaTransactionAsync</c> instead builds the one System Program transfer
    /// itself, with the signer as fee payer and sender, so exactly what is being spent is known by construction.
    /// SPL tokens and arbitrary programs are deliberately not supported. The JSON property names are pinned with
    /// <c>[JsonPropertyName]</c> because this is a wire contract with BiatecMCP's own identical DTO (no shared code).
    /// </summary>
    public sealed class SolanaUnsignedTransfer
    {
        /// <summary>
        /// Solana's base fee for the one-signature legacy transaction <c>DriveService.SignSolanaTransactionAsync</c> builds (no
        /// priority-fee instruction): 5,000 lamports. Defined next to the message shape it is derived from; the spending limit
        /// counts it (<c>WalletService</c>), so a change to what <c>DriveService</c> builds must change this too.
        /// </summary>
        public const ulong BaseFeeLamports = 5000;

        /// <summary>
        /// Largest accepted <see cref="Lamports"/> (<see cref="long.MaxValue"/>, ~9.2 billion SOL - far above Solana's total
        /// supply). Bounding it keeps <c>Lamports + BaseFeeLamports</c> and the USD conversion from ever overflowing, which
        /// would otherwise wrap a huge amount to a tiny priced spend and slip past the spending limit.
        /// </summary>
        public const ulong MaxLamports = long.MaxValue;

        /// <summary>Recipient's base58 public key (the Solana address).</summary>
        [JsonPropertyName("Recipient")]
        public string Recipient { get; set; } = string.Empty;

        /// <summary>Amount to send, in lamports (1 SOL = 1,000,000,000 lamports). Must be greater than 0.</summary>
        [JsonPropertyName("Lamports")]
        public ulong Lamports { get; set; }

        /// <summary>
        /// A recent blockhash (base58) from the target cluster - bounds the transaction's lifetime (~150 slots,
        /// roughly 1-2 minutes) and is the only thing that ties the signed transaction to a particular cluster.
        /// </summary>
        [JsonPropertyName("RecentBlockhash")]
        public string RecentBlockhash { get; set; } = string.Empty;

        /// <summary>
        /// The one validation of the transfer: <see cref="Lamports"/> must be positive and
        /// <see cref="Recipient"/>/<see cref="RecentBlockhash"/> must be base58 32-byte values. Returns the parsed
        /// recipient. The sign endpoint calls it before reading any cloud storage (so a malformed request is a clean 400
        /// rather than being masked by a storage error, a valuation outage or an exhausted limit), <c>WalletService</c>
        /// again before pricing, and <c>DriveService</c> where the transaction is built.
        /// </summary>
        /// <exception cref="FormatException">Lamports is zero, or either key is missing or not a base58 32-byte value.</exception>
        public Solnet.Wallet.PublicKey Validate()
        {
            if (Lamports == 0)
            {
                throw new FormatException("Lamports must be greater than 0.");
            }

            if (Lamports > MaxLamports)
            {
                throw new FormatException($"Lamports must not exceed {MaxLamports}.");
            }

            try
            {
                var recipient = ParseKey(Recipient, "recipient");
                _ = ParseKey(RecentBlockhash, "recentBlockhash");
                return recipient;
            }
            catch (Exception exc)
            {
                throw new FormatException("Unable to parse Solana transfer data: recipient and recentBlockhash must be valid base58 32-byte values.", exc);
            }
        }

        private static Solnet.Wallet.PublicKey ParseKey(string? value, string fieldName)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new FormatException($"{fieldName} is required.");
            }

            var key = new Solnet.Wallet.PublicKey(value);
            if (key.KeyBytes == null || key.KeyBytes.Length != 32)
            {
                throw new FormatException($"{fieldName} must decode to 32 bytes.");
            }

            return key;
        }
    }
}
