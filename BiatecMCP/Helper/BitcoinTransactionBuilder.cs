using BiatecMCP.BusinessLogic;
using BiatecMCP.Model;

namespace BiatecMCP.Helper
{
    /// <summary>
    /// Selects UTXOs, estimates the fee, and assembles a <see cref="BitcoinUnsignedTransaction"/> ready for
    /// <c>POST /wallet/{network}/{address}/sign</c> - pure, no I/O (the UTXO list and fee rate are supplied
    /// by the caller, fetched via <see cref="IPublicBitcoinDataSource"/>). Deliberately does not use
    /// NBitcoin's own coin-selection helpers - a plain greedy largest-first selection is easier to reason
    /// about and test than pulling in a second layer of NBitcoin API surface just for this.
    /// </summary>
    public static class BitcoinTransactionBuilder
    {
        /// <summary>An output below this many satoshis costs more to spend later than it's worth - standard dust threshold for a P2PKH/P2WPKH output.</summary>
        public const long DustThresholdSatoshis = 546;

        // Rough, fixed vsize/size estimates in bytes - good enough for a fee *estimate* (the actual fee is
        // whatever sum(inputs) - sum(outputs) works out to once the outputs below are fixed; this only
        // decides how many inputs to select and whether the fee rate is being honored approximately).
        // Bitcoin figures are vbytes (SegWit v0 P2WPKH keyed input/output, per BIP141's witness discount);
        // Bitcoin Cash has no SegWit, so its figures are plain (uncompressed) legacy P2PKH bytes.
        private const int BtcInputVBytes = 68;
        private const int BtcOutputVBytes = 31;
        private const int BtcOverheadVBytes = 11;
        private const int BchInputBytes = 148;
        private const int BchOutputBytes = 34;
        private const int BchOverheadBytes = 10;

        public sealed class BuildResult
        {
            public BitcoinUnsignedTransaction Transaction { get; set; } = new();
            public long FeeSatoshis { get; set; }
            public long ChangeSatoshis { get; set; }
            public int InputCount { get; set; }
        }

        /// <summary>
        /// Greedily selects UTXOs (largest first) until their sum covers <paramref name="amountSatoshis"/>
        /// plus the estimated fee for however many inputs have been selected so far (recomputed as each one
        /// is added, since fee grows with input count) - simple, but predictable and easy to verify by hand.
        /// Any leftover above <see cref="DustThresholdSatoshis"/> becomes an explicit change output back to
        /// <paramref name="senderAddress"/> (marked <see cref="BitcoinTransactionOutput.IsChange"/>); a
        /// smaller leftover is folded into the fee instead of creating an uneconomical output.
        /// </summary>
        /// <exception cref="InvalidOperationException">The available UTXOs don't cover the amount plus fee.</exception>
        public static BuildResult Build(
            ChainFamily family,
            IReadOnlyList<BitcoinUtxo> availableUtxos,
            string senderAddress,
            string destinationAddress,
            long amountSatoshis,
            decimal feeRateSatoshisPerByte)
        {
            if (amountSatoshis <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(amountSatoshis), "Amount must be positive.");
            }

            var (inputSize, outputSize, overheadSize) = family == ChainFamily.Btc
                ? (BtcInputVBytes, BtcOutputVBytes, BtcOverheadVBytes)
                : (BchInputBytes, BchOutputBytes, BchOverheadBytes);

            var sorted = availableUtxos.OrderByDescending(u => u.AmountSatoshis).ToList();
            var selected = new List<BitcoinUtxo>();
            long selectedTotal = 0;

            foreach (var utxo in sorted)
            {
                selected.Add(utxo);
                selectedTotal += utxo.AmountSatoshis;

                // Assume a change output for the fee estimate while selecting - worst case, it's dropped
                // below and the actual fee ends up a little higher than estimated (never lower, never
                // insufficient).
                var estimatedSize = overheadSize + (selected.Count * inputSize) + (2 * outputSize);
                var estimatedFee = (long)Math.Ceiling(estimatedSize * feeRateSatoshisPerByte);

                if (selectedTotal >= amountSatoshis + estimatedFee)
                {
                    var changeBeforeDust = selectedTotal - amountSatoshis - estimatedFee;
                    var outputs = new List<BitcoinTransactionOutput>
                    {
                        new() { Address = destinationAddress, AmountSatoshis = amountSatoshis }
                    };

                    long actualFee;
                    long change;
                    if (changeBeforeDust >= DustThresholdSatoshis)
                    {
                        outputs.Add(new BitcoinTransactionOutput { Address = senderAddress, AmountSatoshis = changeBeforeDust, IsChange = true });
                        change = changeBeforeDust;
                        actualFee = estimatedFee;
                    }
                    else
                    {
                        // No change output after all (either it doesn't exist or it's dust) - fold whatever
                        // remains into the fee rather than creating an uneconomical output.
                        actualFee = selectedTotal - amountSatoshis;
                        change = 0;
                    }

                    return new BuildResult
                    {
                        Transaction = new BitcoinUnsignedTransaction
                        {
                            Inputs = selected.Select(u => new BitcoinUtxoInput { TxId = u.TxId, Vout = u.Vout, AmountSatoshis = u.AmountSatoshis }).ToList(),
                            Outputs = outputs
                        },
                        FeeSatoshis = actualFee,
                        ChangeSatoshis = change,
                        InputCount = selected.Count
                    };
                }
            }

            throw new InvalidOperationException($"Insufficient funds: available UTXOs total {selectedTotal} satoshis, need at least {amountSatoshis} plus fees.");
        }
    }
}
