namespace BiatecSelfCustodyCore.Model
{
    /// <summary>
    /// An unsigned native-SOL transfer - structured fields rather than a pre-serialized Solana message,
    /// mirroring <see cref="EvmUnsignedTransaction"/>/<see cref="BitcoinUnsignedTransaction"/>'s reasoning, and for a
    /// stronger reason than there: a Solana transaction can carry any number of arbitrary program instructions,
    /// so a pre-serialized blob could hide spend (or an authority change) that nothing here could price or
    /// limit. <c>DriveService.SignSolanaTransactionAsync</c> instead builds the one System Program transfer
    /// itself, with the signer as fee payer and sender, so exactly what is being spent is known by construction.
    /// SPL tokens and arbitrary programs are deliberately not supported.
    /// </summary>
    public sealed class SolanaUnsignedTransfer
    {
        /// <summary>Recipient's base58 public key (the Solana address).</summary>
        public string Recipient { get; set; } = string.Empty;

        /// <summary>Amount to send, in lamports (1 SOL = 1,000,000,000 lamports). Must be greater than 0.</summary>
        public ulong Lamports { get; set; }

        /// <summary>
        /// A recent blockhash (base58) from the target cluster - bounds the transaction's lifetime (~150 slots,
        /// roughly 1-2 minutes) and is the only thing that ties the signed transaction to a particular cluster.
        /// </summary>
        public string RecentBlockhash { get; set; } = string.Empty;

        /// <summary>
        /// Checks <see cref="Recipient"/> and <see cref="RecentBlockhash"/> are base58 32-byte values and returns the
        /// parsed recipient. Called both before pricing/limit-checking (<c>WalletService</c>, so a malformed request
        /// is a clean 400 rather than being masked by a valuation outage or an exhausted limit) and again where the
        /// transaction is built (<c>DriveService</c>).
        /// </summary>
        /// <exception cref="FormatException">Either value is missing or not a base58 32-byte value.</exception>
        public Solnet.Wallet.PublicKey Validate()
        {
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
