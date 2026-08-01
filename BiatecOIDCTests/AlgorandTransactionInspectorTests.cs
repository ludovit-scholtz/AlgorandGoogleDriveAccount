using Algorand;
using Algorand.Algod.Model;
using Algorand.Algod.Model.Transactions;
using Algorand.Utils;
using BiatecOIDC.Helper;

namespace BiatecOIDCTests
{
    [TestFixture]
    public class AlgorandTransactionInspectorTests
    {
        private static readonly Digest TestGenesisHash = new(new byte[32]);

        private static Address NewTestAddress() => new Account().Address;

        [Test]
        public void Inspect_PaymentTransaction_ReturnsPaymentKindAndAmount()
        {
            var addr = NewTestAddress();
            var pay = new PaymentTransaction
            {
                Sender = addr,
                Receiver = addr,
                Amount = 12345,
                Fee = 1000,
                FirstValid = 1,
                LastValid = 1000,
                GenesisId = "testnet-v1.0",
                GenesisHash = TestGenesisHash
            };

            var info = AlgorandTransactionInspector.Inspect(Encoder.EncodeToMsgPackOrdered(pay));

            Assert.That(info.Kind, Is.EqualTo(AlgorandTransactionKind.Payment));
            Assert.That(info.Amount, Is.EqualTo(12345UL));
            Assert.That(info.AssetId, Is.EqualTo(0UL));
        }

        [Test]
        public void Inspect_ZeroAmountPayment_ReturnsZero()
        {
            // Algorand's canonical msgpack encoding omits zero-value fields entirely, so a 0-amount
            // payment has no "amt" key at all on the wire - this must not be mistaken for a decode error.
            var addr = NewTestAddress();
            var pay = new PaymentTransaction
            {
                Sender = addr,
                Receiver = addr,
                Amount = 0,
                Fee = 1000,
                FirstValid = 1,
                LastValid = 1000,
                GenesisId = "testnet-v1.0",
                GenesisHash = TestGenesisHash
            };

            var info = AlgorandTransactionInspector.Inspect(Encoder.EncodeToMsgPackOrdered(pay));

            Assert.That(info.Kind, Is.EqualTo(AlgorandTransactionKind.Payment));
            Assert.That(info.Amount, Is.EqualTo(0UL));
        }

        [Test]
        public void Inspect_AssetTransferTransaction_ReturnsAssetTransferKindAmountAndAssetId()
        {
            var addr = NewTestAddress();
            var axfer = new AssetTransferTransaction
            {
                Sender = addr,
                AssetReceiver = addr,
                AssetAmount = 999,
                XferAsset = 55,
                Fee = 1000,
                FirstValid = 1,
                LastValid = 1000,
                GenesisId = "testnet-v1.0",
                GenesisHash = TestGenesisHash
            };

            var info = AlgorandTransactionInspector.Inspect(Encoder.EncodeToMsgPackOrdered(axfer));

            Assert.That(info.Kind, Is.EqualTo(AlgorandTransactionKind.AssetTransfer));
            Assert.That(info.Amount, Is.EqualTo(999UL));
            Assert.That(info.AssetId, Is.EqualTo(55UL));
        }

        [Test]
        public void Inspect_LargeAmount_DoesNotOverflowSmallMsgPackIntegerTypes()
        {
            // Msgpack encodes integers in the smallest type that fits, so a large amount round-trips as a
            // wider integer type than a small one (e.g. UInt64 vs UInt16) - Inspect must normalize both.
            var addr = NewTestAddress();
            var pay = new PaymentTransaction
            {
                Sender = addr,
                Receiver = addr,
                Amount = 5_000_000_000_000UL,
                Fee = 1000,
                FirstValid = 1,
                LastValid = 1000,
                GenesisId = "testnet-v1.0",
                GenesisHash = TestGenesisHash
            };

            var info = AlgorandTransactionInspector.Inspect(Encoder.EncodeToMsgPackOrdered(pay));

            Assert.That(info.Amount, Is.EqualTo(5_000_000_000_000UL));
        }

        [Test]
        public void Inspect_AssetCreateTransaction_ReturnsOtherKind()
        {
            var addr = NewTestAddress();
            var acfg = new AssetCreateTransaction
            {
                Sender = addr,
                Fee = 1000,
                FirstValid = 1,
                LastValid = 1000,
                GenesisId = "testnet-v1.0",
                GenesisHash = TestGenesisHash
            };

            var info = AlgorandTransactionInspector.Inspect(Encoder.EncodeToMsgPackOrdered(acfg));

            Assert.That(info.Kind, Is.EqualTo(AlgorandTransactionKind.Other));
            Assert.That(info.Amount, Is.EqualTo(0UL));
        }

        [Test]
        public void Inspect_SignedTransactionWrappingPayment_UnwrapsAndReturnsPaymentDetails()
        {
            // Simulates a (partially-)signed co-signing scenario, where the caller sends a
            // SignedTransaction wrapper rather than a bare Transaction - the real transaction fields live
            // one level down, under "txn". The signature itself is irrelevant to Inspect, which only cares
            // about locating and reading the wrapped transaction map.
            var addr = NewTestAddress();
            var pay = new PaymentTransaction
            {
                Sender = addr,
                Receiver = addr,
                Amount = 42,
                Fee = 1000,
                FirstValid = 1,
                LastValid = 1000,
                GenesisId = "testnet-v1.0",
                GenesisHash = TestGenesisHash
            };
            var signedWrapper = new SignedTransaction { Tx = pay };

            var info = AlgorandTransactionInspector.Inspect(Encoder.EncodeToMsgPackOrdered(signedWrapper));

            Assert.That(info.Kind, Is.EqualTo(AlgorandTransactionKind.Payment));
            Assert.That(info.Amount, Is.EqualTo(42UL));
        }

        [Test]
        public void Inspect_PaymentWithRekey_ReturnsIsRekeyTrue()
        {
            var addr = NewTestAddress();
            var pay = new PaymentTransaction
            {
                Sender = addr,
                Receiver = addr,
                Amount = 0,
                Fee = 1000,
                FirstValid = 1,
                LastValid = 1000,
                GenesisId = "testnet-v1.0",
                GenesisHash = TestGenesisHash,
                RekeyTo = NewTestAddress()
            };

            var info = AlgorandTransactionInspector.Inspect(Encoder.EncodeToMsgPackOrdered(pay));

            Assert.That(info.IsRekey, Is.True);
        }

        [Test]
        public void Inspect_PaymentWithoutRekey_ReturnsIsRekeyFalse()
        {
            var addr = NewTestAddress();
            var pay = new PaymentTransaction
            {
                Sender = addr,
                Receiver = addr,
                Amount = 5,
                Fee = 1000,
                FirstValid = 1,
                LastValid = 1000,
                GenesisId = "testnet-v1.0",
                GenesisHash = TestGenesisHash
            };

            var info = AlgorandTransactionInspector.Inspect(Encoder.EncodeToMsgPackOrdered(pay));

            Assert.That(info.IsRekey, Is.False);
        }

        [Test]
        public void Inspect_OtherTransactionKindWithRekey_StillReturnsIsRekeyTrue()
        {
            // A rekey can accompany any transaction type, not just pay/axfer.
            var addr = NewTestAddress();
            var acfg = new AssetCreateTransaction
            {
                Sender = addr,
                Fee = 1000,
                FirstValid = 1,
                LastValid = 1000,
                GenesisId = "testnet-v1.0",
                GenesisHash = TestGenesisHash,
                RekeyTo = NewTestAddress()
            };

            var info = AlgorandTransactionInspector.Inspect(Encoder.EncodeToMsgPackOrdered(acfg));

            Assert.That(info.Kind, Is.EqualTo(AlgorandTransactionKind.Other));
            Assert.That(info.IsRekey, Is.True);
        }

        [Test]
        public void Inspect_SignedTransactionWrappingRekeyPayment_UnwrapsAndReturnsIsRekeyTrue()
        {
            var addr = NewTestAddress();
            var pay = new PaymentTransaction
            {
                Sender = addr,
                Receiver = addr,
                Amount = 1,
                Fee = 1000,
                FirstValid = 1,
                LastValid = 1000,
                GenesisId = "testnet-v1.0",
                GenesisHash = TestGenesisHash,
                RekeyTo = NewTestAddress()
            };
            var signedWrapper = new SignedTransaction { Tx = pay };

            var info = AlgorandTransactionInspector.Inspect(Encoder.EncodeToMsgPackOrdered(signedWrapper));

            Assert.That(info.IsRekey, Is.True);
        }

        [Test]
        public void Inspect_EmptyBytes_ThrowsFormatException()
        {
            Assert.Throws<FormatException>(() => AlgorandTransactionInspector.Inspect(Array.Empty<byte>()));
        }

        [Test]
        public void Inspect_GarbageBytes_ThrowsFormatException()
        {
            Assert.Throws<FormatException>(() => AlgorandTransactionInspector.Inspect(new byte[] { 0xFF, 0x00, 0x01, 0x02 }));
        }

        [Test]
        public void Inspect_ValidMsgPackButNotATransactionMap_ThrowsFormatException()
        {
            // A valid msgpack-encoded value (a plain string) that isn't a transaction map at all.
            var bytes = MessagePack.MessagePackSerializer.Serialize("not a transaction");

            Assert.Throws<FormatException>(() => AlgorandTransactionInspector.Inspect(bytes));
        }
    }
}
