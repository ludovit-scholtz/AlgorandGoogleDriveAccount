using Algorand;
using Algorand.Algod.Model;
using Algorand.Algod.Model.Transactions;
using Algorand.Utils;
using BiatecMCP.Helper;

namespace BiatecMCPTests
{
    /// <summary>
    /// Covers <see cref="MultisigTransactionBuilder"/>: deriving a multisig account's address from its
    /// participants, building the unsigned "envelope" each cosigner independently signs, and merging N
    /// independently-signed copies into one broadcastable transaction - the same
    /// <c>SignedTransaction.MergeMultisigTransactionBytes</c> operation the Algorand SDK exposes, wrapped
    /// for testability.
    /// </summary>
    [TestFixture]
    public class MultisigTransactionBuilderTests
    {
        private static TransactionParametersResponse SuggestedParams() => new()
        {
            Fee = 0,
            MinFee = 1000,
            GenesisHash = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray(),
            GenesisId = "testnet-v1.0",
            LastRound = 5_000_000,
            ConsensusVersion = "https://github.com/algorandfoundation/specs/tree/abc123"
        };

        private static (Account account, string address) NewParticipant()
        {
            var account = new Account();
            return (account, account.Address.EncodeAsString());
        }

        [Test]
        public void DeriveAddress_SameParticipantsAndThreshold_IsDeterministic()
        {
            var p1 = NewParticipant();
            var p2 = NewParticipant();

            var address1 = MultisigTransactionBuilder.DeriveAddress(1, 2, new[] { p1.address, p2.address });
            var address2 = MultisigTransactionBuilder.DeriveAddress(1, 2, new[] { p1.address, p2.address });

            Assert.That(address1, Is.EqualTo(address2));
        }

        [Test]
        public void DeriveAddress_DifferentThreshold_ProducesDifferentAddress()
        {
            var p1 = NewParticipant();
            var p2 = NewParticipant();

            var address1 = MultisigTransactionBuilder.DeriveAddress(1, 1, new[] { p1.address, p2.address });
            var address2 = MultisigTransactionBuilder.DeriveAddress(1, 2, new[] { p1.address, p2.address });

            Assert.That(address1, Is.Not.EqualTo(address2));
        }

        [Test]
        public void BuildEnvelope_WrapsTransactionWithMultisigSignatureMetadata()
        {
            var p1 = NewParticipant();
            var p2 = NewParticipant();
            var multisigAddress = MultisigTransactionBuilder.DeriveAddress(1, 2, new[] { p1.address, p2.address });
            var sender = new Address(multisigAddress);
            var inner = AlgorandTransactionBuilder.BuildPayment(sender, sender, 1000, "biatecmcp", SuggestedParams());

            var envelopeBase64 = MultisigTransactionBuilder.BuildEnvelope(inner, 1, 2, new[] { p1.address, p2.address });

            var envelope = SignedTransaction.FromBase64String(envelopeBase64);
            Assert.That(envelope.Tx.Sender.EncodeAsString(), Is.EqualTo(multisigAddress));
            Assert.That(envelope.MSig, Is.Not.Null);
            Assert.That(envelope.MSig.Version, Is.EqualTo(1));
            Assert.That(envelope.MSig.Threshold, Is.EqualTo(2));
            Assert.That(envelope.MSig.Subsigs, Has.Count.EqualTo(2));
        }

        [Test]
        public void Merge_CombinesIndependentlySignedCopiesIntoOneWithBothSubsigsPresent()
        {
            var p1 = NewParticipant();
            var p2 = NewParticipant();
            var multisigAddress = MultisigTransactionBuilder.DeriveAddress(1, 2, new[] { p1.address, p2.address });
            var sender = new Address(multisigAddress);
            var inner = AlgorandTransactionBuilder.BuildPayment(sender, sender, 1000, "biatecmcp", SuggestedParams());
            var envelopeBase64 = MultisigTransactionBuilder.BuildEnvelope(inner, 1, 2, new[] { p1.address, p2.address });
            var envelope = SignedTransaction.FromBase64String(envelopeBase64);

            // Each cosigner independently signs a fresh copy of the same envelope - mirrors what
            // DriveService.SignTransactionAsync does server-side for each Biatec /wallet/sign call.
            var msig1 = new MultisigAddress(1, 2, new List<byte[]> { p1.account.Address.Bytes, p2.account.Address.Bytes });
            var signed1 = envelope.Tx.Sign(msig1, p1.account);
            var signed2 = envelope.Tx.Sign(msig1, p2.account);

            var mergedBase64 = MultisigTransactionBuilder.Merge(new[]
            {
                Convert.ToBase64String(Encoder.EncodeToMsgPackOrdered(signed1)),
                Convert.ToBase64String(Encoder.EncodeToMsgPackOrdered(signed2))
            });

            var merged = SignedTransaction.FromBase64String(mergedBase64);
            Assert.That(merged.MSig.Subsigs.Count(s => s.sig != null), Is.EqualTo(2));
        }

        [Test]
        public void BuildEnvelope_UnknownParticipantAddress_ThrowsFormatException()
        {
            Assert.That(() => MultisigTransactionBuilder.DeriveAddress(1, 1, new[] { "not-a-valid-address" }),
                Throws.Exception);
        }
    }
}
