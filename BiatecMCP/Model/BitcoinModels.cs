namespace BiatecMCP.Model
{
    /// <summary>
    /// One UTXO being spent - mirrors <c>BiatecSelfCustodyCore.Model.BitcoinUtxoInput</c> field-for-field
    /// (same reasoning as every other duplicated wallet-API DTO in this project: no compile-time coupling
    /// between the two independently-deployed services, just a matching JSON shape). Serialized as the
    /// base64 body of one entry in <c>SignTransactionGroupRequest.Transactions</c> for a Bitcoin/Bitcoin
    /// Cash <c>network</c>.
    /// </summary>
    public sealed class BitcoinUtxoInput
    {
        public string TxId { get; set; } = string.Empty;
        public uint Vout { get; set; }
        public long AmountSatoshis { get; set; }
    }

    /// <summary>Mirrors <c>BiatecSelfCustodyCore.Model.BitcoinTransactionOutput</c>.</summary>
    public sealed class BitcoinTransactionOutput
    {
        public string Address { get; set; } = string.Empty;
        public long AmountSatoshis { get; set; }

        /// <summary>Whether this output returns change to the sender rather than paying a recipient - excluded from BiatecOIDC's spending-limit valuation.</summary>
        public bool IsChange { get; set; }
    }

    /// <summary>Mirrors <c>BiatecSelfCustodyCore.Model.BitcoinUnsignedTransaction</c>.</summary>
    public sealed class BitcoinUnsignedTransaction
    {
        public List<BitcoinUtxoInput> Inputs { get; set; } = new();

        public List<BitcoinTransactionOutput> Outputs { get; set; } = new();
    }
}
