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
    }
}
