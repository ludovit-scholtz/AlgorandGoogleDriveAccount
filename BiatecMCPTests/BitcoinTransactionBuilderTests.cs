using BiatecMCP.BusinessLogic;
using BiatecMCP.Helper;

namespace BiatecMCPTests
{
    /// <summary>
    /// Covers <see cref="BitcoinTransactionBuilder"/>'s coin selection, fee estimation, and change/dust
    /// handling - pure logic, no I/O (UTXOs and fee rate are supplied directly).
    /// </summary>
    [TestFixture]
    public class BitcoinTransactionBuilderTests
    {
        private const string SenderAddress = "bc1qsender0000000000000000000000000000000";
        private const string DestinationAddress = "bc1qdest00000000000000000000000000000000000";

        [Test]
        public void Build_SingleUtxoCoversAmountAndFee_SelectsOneInputWithChange()
        {
            var utxos = new[] { new BitcoinUtxo("tx1", 0, 1_000_000) };

            var result = BitcoinTransactionBuilder.Build(ChainFamily.Btc, utxos, SenderAddress, DestinationAddress, 500_000, feeRateSatoshisPerByte: 1m);

            Assert.That(result.InputCount, Is.EqualTo(1));
            Assert.That(result.Transaction.Inputs, Has.Count.EqualTo(1));
            Assert.That(result.Transaction.Outputs, Has.Count.EqualTo(2));
            var destOutput = result.Transaction.Outputs.Single(o => !o.IsChange);
            var changeOutput = result.Transaction.Outputs.Single(o => o.IsChange);
            Assert.That(destOutput.Address, Is.EqualTo(DestinationAddress));
            Assert.That(destOutput.AmountSatoshis, Is.EqualTo(500_000));
            Assert.That(changeOutput.Address, Is.EqualTo(SenderAddress));
            Assert.That(changeOutput.AmountSatoshis, Is.EqualTo(1_000_000 - 500_000 - result.FeeSatoshis));
            Assert.That(result.FeeSatoshis, Is.GreaterThan(0));
        }

        [Test]
        public void Build_ChangeWouldBeDust_FoldsChangeIntoFeeInstead()
        {
            // Selected input is only just enough to cover the amount plus fee plus a dust-sized remainder -
            // the change output must be dropped, not created as an uneconomical dust output.
            var utxos = new[] { new BitcoinUtxo("tx1", 0, 500_200) };

            var result = BitcoinTransactionBuilder.Build(ChainFamily.Btc, utxos, SenderAddress, DestinationAddress, 500_000, feeRateSatoshisPerByte: 1m);

            Assert.That(result.Transaction.Outputs, Has.Count.EqualTo(1));
            Assert.That(result.ChangeSatoshis, Is.EqualTo(0));
            Assert.That(result.FeeSatoshis, Is.EqualTo(200));
        }

        [Test]
        public void Build_MultipleUtxosNeeded_SelectsLargestFirstUntilCovered()
        {
            var utxos = new[]
            {
                new BitcoinUtxo("small", 0, 100_000),
                new BitcoinUtxo("large", 0, 900_000),
                new BitcoinUtxo("medium", 0, 400_000)
            };

            var result = BitcoinTransactionBuilder.Build(ChainFamily.Btc, utxos, SenderAddress, DestinationAddress, 1_000_000, feeRateSatoshisPerByte: 1m);

            // Largest-first: "large" (900k) alone isn't enough for 1,000,000 + fee, so "medium" (400k) is
            // added next - "small" should never be needed.
            Assert.That(result.InputCount, Is.EqualTo(2));
            Assert.That(result.Transaction.Inputs.Select(i => i.TxId), Is.EquivalentTo(new[] { "large", "medium" }));
        }

        [Test]
        public void Build_InsufficientFunds_ThrowsInvalidOperationException()
        {
            var utxos = new[] { new BitcoinUtxo("tx1", 0, 100) };

            Assert.Throws<InvalidOperationException>(() =>
                BitcoinTransactionBuilder.Build(ChainFamily.Btc, utxos, SenderAddress, DestinationAddress, 1_000_000, feeRateSatoshisPerByte: 1m));
        }

        [Test]
        public void Build_ZeroOrNegativeAmount_ThrowsArgumentOutOfRangeException()
        {
            var utxos = new[] { new BitcoinUtxo("tx1", 0, 1_000_000) };

            Assert.Throws<ArgumentOutOfRangeException>(() =>
                BitcoinTransactionBuilder.Build(ChainFamily.Btc, utxos, SenderAddress, DestinationAddress, 0, feeRateSatoshisPerByte: 1m));
        }

        [Test]
        public void Build_BitcoinCash_UsesLegacyNonSegwitSizeEstimate_HigherFeeThanBitcoinForSameInputs()
        {
            var utxos = new[] { new BitcoinUtxo("tx1", 0, 1_000_000) };

            var btcResult = BitcoinTransactionBuilder.Build(ChainFamily.Btc, utxos, SenderAddress, DestinationAddress, 500_000, feeRateSatoshisPerByte: 1m);
            var bchResult = BitcoinTransactionBuilder.Build(ChainFamily.Bch, utxos, SenderAddress, DestinationAddress, 500_000, feeRateSatoshisPerByte: 1m);

            // Bitcoin Cash has no SegWit discount, so the same 1-input/2-output shape costs more at the same
            // sat/byte rate.
            Assert.That(bchResult.FeeSatoshis, Is.GreaterThan(btcResult.FeeSatoshis));
        }
    }
}
