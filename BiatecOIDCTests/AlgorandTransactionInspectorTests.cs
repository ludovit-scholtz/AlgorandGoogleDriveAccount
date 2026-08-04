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
        public void Inspect_PaymentTransaction_ReturnsSenderAddress()
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
                GenesisHash = TestGenesisHash
            };

            var info = AlgorandTransactionInspector.Inspect(Encoder.EncodeToMsgPackOrdered(pay));

            Assert.That(info.Sender, Is.EqualTo(addr.EncodeAsString()));
        }

        [Test]
        public void Inspect_SignedTransactionWrappingPayment_ReturnsSenderFromWrappedTxn()
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
                GenesisHash = TestGenesisHash
            };
            var signedWrapper = new SignedTransaction { Tx = pay };

            var info = AlgorandTransactionInspector.Inspect(Encoder.EncodeToMsgPackOrdered(signedWrapper));

            Assert.That(info.Sender, Is.EqualTo(addr.EncodeAsString()));
        }

        [Test]
        public void Inspect_BareTransaction_IsNotMultisig()
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
                GenesisHash = TestGenesisHash
            };

            var info = AlgorandTransactionInspector.Inspect(Encoder.EncodeToMsgPackOrdered(pay));

            Assert.That(info.IsMultisig, Is.False);
        }

        [Test]
        public void Inspect_SignedTransactionWithoutMsig_IsNotMultisig()
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
                GenesisHash = TestGenesisHash
            };
            var signedWrapper = new SignedTransaction { Tx = pay };

            var info = AlgorandTransactionInspector.Inspect(Encoder.EncodeToMsgPackOrdered(signedWrapper));

            Assert.That(info.IsMultisig, Is.False);
        }

        [Test]
        public void Inspect_SignedTransactionWithMsig_IsMultisig()
        {
            var addr = NewTestAddress();
            var participant2 = NewTestAddress();
            var pay = new PaymentTransaction
            {
                Sender = addr,
                Receiver = addr,
                Amount = 1,
                Fee = 1000,
                FirstValid = 1,
                LastValid = 1000,
                GenesisId = "testnet-v1.0",
                GenesisHash = TestGenesisHash
            };
            var subsigs = new List<MultisigSubsig>
            {
                new(addr.Bytes, null),
                new(participant2.Bytes, null)
            };
            var signedWrapper = new SignedTransaction { Tx = pay, MSig = new MultisigSignature(1, 2, subsigs) };

            var info = AlgorandTransactionInspector.Inspect(Encoder.EncodeToMsgPackOrdered(signedWrapper));

            Assert.That(info.IsMultisig, Is.True);
        }

        [Test]
        public void Inspect_PaymentWithCloseRemainderTo_ReturnsIsCloseOutTrue()
        {
            // Audit finding H-01/R-024: a close-remainder-to payment sweeps the sender's entire remaining
            // balance regardless of "amt", and must be flagged so WalletController refuses to sign it
            // without the 'rekey' claim rather than pricing it at whatever "amt" happens to be.
            var addr = NewTestAddress();
            var pay = new PaymentTransaction
            {
                Sender = addr,
                Receiver = addr,
                Amount = 0,
                Fee = 1000,
                FirstValid = 1,
                LastValid = 1000,
                GenesisId = "mainnet-v1.0",
                GenesisHash = TestGenesisHash,
                CloseRemainderTo = NewTestAddress()
            };

            var info = AlgorandTransactionInspector.Inspect(Encoder.EncodeToMsgPackOrdered(pay));

            Assert.That(info.IsCloseOut, Is.True);
            Assert.That(info.Amount, Is.EqualTo(0UL));
        }

        [Test]
        public void Inspect_PaymentWithoutCloseRemainderTo_ReturnsIsCloseOutFalse()
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

            Assert.That(info.IsCloseOut, Is.False);
        }

        [Test]
        public void Inspect_SignedTransactionWrappingCloseRemainderToPayment_UnwrapsAndReturnsIsCloseOutTrue()
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
                GenesisId = "mainnet-v1.0",
                GenesisHash = TestGenesisHash,
                CloseRemainderTo = NewTestAddress()
            };
            var signedWrapper = new SignedTransaction { Tx = pay };

            var info = AlgorandTransactionInspector.Inspect(Encoder.EncodeToMsgPackOrdered(signedWrapper));

            Assert.That(info.IsCloseOut, Is.True);
        }

        [Test]
        public void Inspect_AssetTransferWithAcloseFieldOnTheWire_ReturnsIsCloseOutTrue()
        {
            // The pinned Algorand4 SDK's AssetTransferTransaction has no settable AssetCloseTo property (an
            // "aclose" transaction can still legally appear on the wire, e.g. from a non-SDK-built caller),
            // so this constructs the raw msgpack map directly rather than via the typed SDK object, to prove
            // Inspect reads "aclose" independent of what the local SDK version happens to model.
            var addr = NewTestAddress();
            var closeTo = NewTestAddress();
            var map = new Dictionary<string, object>
            {
                ["type"] = "axfer",
                ["snd"] = addr.Bytes,
                ["arcv"] = addr.Bytes,
                ["aamt"] = 0,
                ["xaid"] = 55,
                ["aclose"] = closeTo.Bytes,
                ["fee"] = 1000,
                ["fv"] = 1,
                ["lv"] = 1000,
                ["gen"] = "mainnet-v1.0",
                ["gh"] = TestGenesisHash.Bytes
            };
            var bytes = MessagePack.MessagePackSerializer.Serialize(map, MessagePack.MessagePackSerializerOptions.Standard.WithResolver(MessagePack.Resolvers.ContractlessStandardResolver.Instance));

            var info = AlgorandTransactionInspector.Inspect(bytes);

            Assert.That(info.Kind, Is.EqualTo(AlgorandTransactionKind.AssetTransfer));
            Assert.That(info.IsCloseOut, Is.True);
            Assert.That(info.Amount, Is.EqualTo(0UL));
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
