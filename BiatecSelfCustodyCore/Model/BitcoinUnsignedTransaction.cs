namespace BiatecSelfCustodyCore.Model
{
    /// <summary>
    /// Which Bitcoin-family chain a <see cref="BitcoinUnsignedTransaction"/> is for - determines both the
    /// address format (Bech32 vs CashAddr) and the sighash algorithm used to sign it (BIP143 vs Bitcoin
    /// Cash's replay-protected SIGHASH_FORKID) - see <c>DriveService.SignBitcoinTransactionAsync</c>.
    /// </summary>
    public enum BitcoinChainFamily
    {
        Bitcoin,
        BitcoinCash
    }

    /// <summary>
    /// One UTXO being spent - always one of the signing seed/slot's own previously-received outputs (this
    /// wallet never signs someone else's input), so its scriptPubKey isn't carried on the wire - it's
    /// reconstructed from the signer's own derived address (see <c>DriveService.SignBitcoinTransactionAsync</c>)
    /// instead of trusting a caller-supplied one.
    /// </summary>
    public sealed class BitcoinUtxoInput
    {
        /// <summary>The funding transaction's id (hex, big-endian display order - as returned by any block explorer).</summary>
        public string TxId { get; set; } = string.Empty;

        /// <summary>Index of the output being spent within <see cref="TxId"/>.</summary>
        public uint Vout { get; set; }

        /// <summary>The output's value, in satoshis - required to compute the correct sighash preimage (BIP143/ForkId), not just informational.</summary>
        public long AmountSatoshis { get; set; }
    }

    /// <summary>One payment output - the recipient, or the sender's own change address.</summary>
    public sealed class BitcoinTransactionOutput
    {
        /// <summary>Destination address, in whichever format the target chain uses (Base58Check/Bech32 for Bitcoin, CashAddr or legacy for Bitcoin Cash).</summary>
        public string Address { get; set; } = string.Empty;

        /// <summary>Amount to send, in satoshis.</summary>
        public long AmountSatoshis { get; set; }

        /// <summary>
        /// Whether this output returns change to the sender's own address rather than paying a recipient -
        /// set by <c>BiatecMCP</c>'s <c>BitcoinTransactionBuilder</c> during coin selection. Excluded from
        /// the spending-limit valuation (<c>WalletService.SignBitcoinTransactionGroupAsync</c>) - money
        /// staying in the sender's own wallet was never actually spent.
        /// </summary>
        public bool IsChange { get; set; }
    }

    /// <summary>
    /// An unsigned Bitcoin or Bitcoin Cash transaction - a plain list of inputs/outputs rather than a raw
    /// encoded blob, mirroring <see cref="EvmUnsignedTransaction"/>'s reasoning: the piece that knows how to
    /// safely build the real NBitcoin <c>Transaction</c>/sign it (<c>DriveService</c>) needs the fields, not
    /// something to decode. Coin selection, change calculation, and fee estimation all happen before this is
    /// built (<c>BiatecMCP</c>'s <c>BitcoinTransactionBuilder</c>) - by the time this reaches signing, the
    /// implicit fee is simply <c>sum(Inputs) - sum(Outputs)</c>, and every output (including change) is
    /// already explicit.
    /// </summary>
    public sealed class BitcoinUnsignedTransaction
    {
        public List<BitcoinUtxoInput> Inputs { get; set; } = new();

        public List<BitcoinTransactionOutput> Outputs { get; set; } = new();
    }
}
