using Algorand;
using Algorand.Algod.Model;
using Algorand.Algod.Model.Transactions;
using Algorand.Utils;
using BiatecMCP.Helper;

namespace BiatecMCPTests
{
    [TestFixture]
    public class AlgorandTransactionBuilderTests
    {
        // Two arbitrary, valid (correct checksum) Algorand addresses - not associated with any real key material.
        private const string SenderAddress = "I3IINASAS7SKHFOP75DGTHDTYSQ42EBUCNNU5I3PQSSUVX32B2QIOTXIWU";
        private const string ReceiverAddress = "OGJ4NFJEXHRW67BYTC352IALYELB53BO6C6RMRXSVHTJG3DCNEZUASYYJE";

        private static TransactionParametersResponse SuggestedParams() => new()
        {
            Fee = 0,
            MinFee = 1000,
            GenesisHash = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray(),
            GenesisId = "testnet-v1.0",
            LastRound = 5_000_000,
            ConsensusVersion = "https://github.com/algorandfoundation/specs/tree/abc123"
        };

        [Test]
        public void BuildPayment_EncodesSenderReceiverAmountAndNote()
        {
            var sender = new Address(SenderAddress);
            var receiver = new Address(ReceiverAddress);

            var bytes = AlgorandTransactionBuilder.BuildPayment(sender, receiver, 1_000_000, "biatecmcp", SuggestedParams());

            var decoded = Encoder.DecodeFromMsgPack<PaymentTransaction>(bytes);
            Assert.That(decoded.Sender.EncodeAsString(), Is.EqualTo(SenderAddress));
            Assert.That(decoded.Receiver.EncodeAsString(), Is.EqualTo(ReceiverAddress));
            Assert.That(decoded.Amount, Is.EqualTo(1_000_000UL));
            Assert.That(decoded.Note, Is.Not.Null);
            Assert.That(System.Text.Encoding.UTF8.GetString(decoded.Note!), Is.EqualTo("biatecmcp"));
        }

        [Test]
        public void BuildPayment_SelfTransfer_SenderEqualsReceiver()
        {
            var sender = new Address(SenderAddress);

            var bytes = AlgorandTransactionBuilder.BuildPayment(sender, sender, 1_000_000, "", SuggestedParams());

            var decoded = Encoder.DecodeFromMsgPack<PaymentTransaction>(bytes);
            Assert.That(decoded.Sender.EncodeAsString(), Is.EqualTo(SenderAddress));
            Assert.That(decoded.Receiver.EncodeAsString(), Is.EqualTo(SenderAddress));
        }

        [Test]
        public void BuildPayment_FillsFeeAndValidRoundRangeFromSuggestedParams()
        {
            var sender = new Address(SenderAddress);
            var suggestedParams = SuggestedParams();

            var bytes = AlgorandTransactionBuilder.BuildPayment(sender, sender, 1, null, suggestedParams);

            var decoded = Encoder.DecodeFromMsgPack<PaymentTransaction>(bytes);
            Assert.That(decoded.Fee, Is.GreaterThanOrEqualTo(suggestedParams.MinFee));
            Assert.That(decoded.FirstValid, Is.GreaterThanOrEqualTo(suggestedParams.LastRound));
            Assert.That(decoded.LastValid, Is.GreaterThan(decoded.FirstValid));
            Assert.That(decoded.GenesisId, Is.EqualTo(suggestedParams.GenesisId));
        }

        [Test]
        public void BuildAssetTransfer_EncodesAssetIdAmountAndReceiver()
        {
            var sender = new Address(SenderAddress);
            var receiver = new Address(ReceiverAddress);

            var bytes = AlgorandTransactionBuilder.BuildAssetTransfer(sender, receiver, assetId: 12345, amount: 500, note: "note-here", SuggestedParams());

            var decoded = Encoder.DecodeFromMsgPack<AssetTransferTransaction>(bytes);
            Assert.That(decoded.Sender.EncodeAsString(), Is.EqualTo(SenderAddress));
            Assert.That(decoded.AssetReceiver.EncodeAsString(), Is.EqualTo(ReceiverAddress));
            Assert.That(decoded.XferAsset, Is.EqualTo(12345UL));
            Assert.That(decoded.AssetAmount, Is.EqualTo(500UL));
            Assert.That(System.Text.Encoding.UTF8.GetString(decoded.Note!), Is.EqualTo("note-here"));
        }

        [Test]
        public void BuildOptIn_IsAZeroAmountSelfAssetTransfer()
        {
            var sender = new Address(SenderAddress);

            var bytes = AlgorandTransactionBuilder.BuildOptIn(sender, assetId: 999, note: null, SuggestedParams());

            var decoded = Encoder.DecodeFromMsgPack<AssetTransferTransaction>(bytes);
            Assert.That(decoded.Sender.EncodeAsString(), Is.EqualTo(SenderAddress));
            Assert.That(decoded.AssetReceiver.EncodeAsString(), Is.EqualTo(SenderAddress));
            Assert.That(decoded.XferAsset, Is.EqualTo(999UL));
            Assert.That(decoded.AssetAmount, Is.EqualTo(0UL));
        }

        [Test]
        public void BuildAssetTransfer_EmptyNote_ProducesNoNoteBytes()
        {
            var sender = new Address(SenderAddress);

            var bytes = AlgorandTransactionBuilder.BuildAssetTransfer(sender, sender, assetId: 1, amount: 0, note: "", SuggestedParams());

            var decoded = Encoder.DecodeFromMsgPack<AssetTransferTransaction>(bytes);
            Assert.That(decoded.Note, Is.Null.Or.Empty);
        }
    }
}
