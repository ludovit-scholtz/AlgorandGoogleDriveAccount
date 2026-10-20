using System.Buffers.Binary;
using Algorand;
using Algorand.Algod.Model;
using Algorand.Algod.Model.Transactions;
using BiatecMCP.BusinessLogic.AlgoSafe;
using Moq;

namespace BiatecMCPTests
{
    [TestFixture]
    public class AlgoSafePayloadCodecTests
    {
        private static readonly Address Alice = new("GD64YIY3TWGDMCNPP553DZPPR6LDUSFQOIJVFDPPXWEG3FVOJCCDBBHU5A");
        private static readonly Address Bob = new("7QLMZH5C5XVPEV6T7D6W2TMWRSURHVDMLQEBDODWGVVMKOJD2A77AIHVMA");

        [Test]
        public void Payment_RoundTrips()
        {
            var bytes = AlgoSafePayloadCodec.EncodePayment(AlgoSafePayloadCodec.ZeroAddress, Bob, 500_000, 0, AlgoSafePayloadCodec.ZeroAddress, "hello");
            var decoded = AlgoSafePayloadCodec.DecodePayment(bytes);

            Assert.That(decoded.Receiver, Is.EqualTo(Bob));
            Assert.That(decoded.Amount, Is.EqualTo(500_000UL));
            Assert.That(decoded.Note, Is.EqualTo("hello"));
        }

        [Test]
        public void Asset_RoundTrips()
        {
            var bytes = AlgoSafePayloadCodec.EncodeAsset(AlgoSafePayloadCodec.ZeroAddress, 31566704, Bob, 42, 0, AlgoSafePayloadCodec.ZeroAddress, "");
            var decoded = AlgoSafePayloadCodec.DecodeAsset(bytes);

            Assert.That(decoded.XferAsset, Is.EqualTo(31566704UL));
            Assert.That(decoded.AssetReceiver, Is.EqualTo(Bob));
            Assert.That(decoded.AssetAmount, Is.EqualTo(42UL));
        }

        [Test]
        public void EnvelopeArray_UsesArc4DynamicTupleLayout_AndRoundTrips()
        {
            var first = AlgoSafePayloadCodec.EncodePayment(AlgoSafePayloadCodec.ZeroAddress, Bob, 1, 0, AlgoSafePayloadCodec.ZeroAddress, "");
            var second = AlgoSafePayloadCodec.EncodeAsset(AlgoSafePayloadCodec.ZeroAddress, 7, Bob, 2, 0, AlgoSafePayloadCodec.ZeroAddress, "");
            var entries = new List<AlgoSafePayloadEntry> { new(1, first), new(2, second) };

            var encoded = AlgoSafePayloadCodec.EncodeEnvelopeArray(entries);

            // length prefix, then one 2-byte offset per element (relative to the end of the length prefix) - the part the generated client got wrong
            Assert.That(BinaryPrimitives.ReadUInt16BigEndian(encoded.AsSpan(0, 2)), Is.EqualTo(2));
            Assert.That(BinaryPrimitives.ReadUInt16BigEndian(encoded.AsSpan(2, 2)), Is.EqualTo(4));
            var secondOffset = BinaryPrimitives.ReadUInt16BigEndian(encoded.AsSpan(4, 2));
            Assert.That(secondOffset, Is.EqualTo(4 + 8 + 2 + 2 + first.Length));

            var decoded = AlgoSafePayloadCodec.DecodeEnvelopeArray(encoded);
            Assert.That(decoded, Has.Count.EqualTo(2));
            Assert.That(decoded[0].TxType, Is.EqualTo(1UL));
            Assert.That(decoded[0].Data, Is.EqualTo(first));
            Assert.That(decoded[1].TxType, Is.EqualTo(2UL));
            Assert.That(decoded[1].Data, Is.EqualTo(second));
        }

        [Test]
        public void MembersArray_UsesArc4DynamicTupleLayout()
        {
            var encoded = AlgoSafePayloadCodec.EncodeMembersArray(new List<(Address, ulong, string)> { (Alice, 1, "alice"), (Bob, 1, "bob") });

            Assert.That(BinaryPrimitives.ReadUInt16BigEndian(encoded.AsSpan(0, 2)), Is.EqualTo(2));
            Assert.That(BinaryPrimitives.ReadUInt16BigEndian(encoded.AsSpan(2, 2)), Is.EqualTo(4));

            // element 0 = address(32) | accountType(8) | offset(2)=42 | len(2) | label
            var element0 = 2 + 4;
            Assert.That(encoded.AsSpan(element0, 32).ToArray(), Is.EqualTo(Alice.Bytes));
            Assert.That(BinaryPrimitives.ReadUInt16BigEndian(encoded.AsSpan(element0 + 40, 2)), Is.EqualTo(42));
            Assert.That(BinaryPrimitives.ReadUInt16BigEndian(encoded.AsSpan(4, 2)), Is.EqualTo(4 + 42 + 2 + "alice".Length));
        }
    }

    [TestFixture]
    public class AlgoSafeResourcePopulatorTests
    {
        private static readonly Address Sender = new("GD64YIY3TWGDMCNPP553DZPPR6LDUSFQOIJVFDPPXWEG3FVOJCCDBBHU5A");
        private static readonly Address Other = new("7QLMZH5C5XVPEV6T7D6W2TMWRSURHVDMLQEBDODWGVVMKOJD2A77AIHVMA");

        private static ApplicationNoopTransaction NewCall(ulong appId) => new()
        {
            Sender = Sender,
            ApplicationId = appId,
            Fee = 1000,
            FirstValid = 100,
            LastValid = 200,
            GenesisHash = new Digest(new byte[32]),
            GenesisId = "testnet-v1.0"
        };

        [Test]
        public async Task FeeTooSmall_EscalatesThenTrimsToTheExactInnerTransactionFee()
        {
            var call = NewCall(55);
            var chain = new Mock<IAlgoSafeChainClient>();
            var calls = 0;
            chain.Setup(c => c.SimulateAsync(It.IsAny<IReadOnlyList<Transaction>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() =>
                {
                    calls++;
                    // first the node refuses (fee covers only the app call); once raised it runs and shows 3 inner transactions
                    return call.Fee < 4000
                        ? new SimulateTransactionGroupResult { FailureMessage = "transaction group: fee 1000 too small (needs 3000 more)" }
                        : Succeeded(innerTransactions: 3);
                });

            var result = await AlgoSafeResourcePopulator.PopulateAsync(chain.Object, new List<Transaction> { call }, 1000);

            Assert.That(result.InnerTransactionCount, Is.EqualTo(3));
            Assert.That(call.Fee, Is.EqualTo(4000UL), "1 call + 3 inner transactions, one min fee each");
            Assert.That(result.TotalFeeMicroAlgo, Is.EqualTo(4000UL));
            Assert.That(calls, Is.GreaterThan(1));
        }

        [Test]
        public async Task UnnamedResources_AreAddedAsReferences_AndSimulationRepeatsUntilStable()
        {
            var call = NewCall(55);
            var chain = new Mock<IAlgoSafeChainClient>();
            chain.Setup(c => c.SimulateAsync(It.IsAny<IReadOnlyList<Transaction>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => call.Accounts?.Contains(Other) == true
                    ? Succeeded(0)
                    : Succeeded(0, new SimulateUnnamedResourcesAccessed { Accounts = new List<Address> { Other }, Assets = new List<ulong> { 31566704 } }));

            await AlgoSafeResourcePopulator.PopulateAsync(chain.Object, new List<Transaction> { call }, 1000);

            Assert.That(call.Accounts, Does.Contain(Other));
            Assert.That(call.ForeignAssets, Does.Contain(31566704UL));
        }

        [Test]
        public async Task MoreAccountsThanOneCallCanCarry_FailsWithAClearError()
        {
            var call = NewCall(55);
            var many = Enumerable.Range(0, 5).Select(i => Address.ForApplication((ulong)(1000 + i))).ToList();
            var chain = new Mock<IAlgoSafeChainClient>();
            chain.Setup(c => c.SimulateAsync(It.IsAny<IReadOnlyList<Transaction>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Succeeded(0, new SimulateUnnamedResourcesAccessed { Accounts = many }));

            var ex = await Assert.ThrowsAsync<AlgoSafeException>(() => AlgoSafeResourcePopulator.PopulateAsync(chain.Object, new List<Transaction> { call }, 1000));

            Assert.That(ex!.ErrorType, Is.EqualTo("TooManyReferences"));
        }

        [Test]
        public async Task ARealFailure_IsSurfacedWithoutTheStructDump()
        {
            var call = NewCall(55);
            var chain = new Mock<IAlgoSafeChainClient>();
            chain.Setup(c => c.SimulateAsync(It.IsAny<IReadOnlyList<Transaction>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new SimulateTransactionGroupResult { FailureMessage = "transaction ABC: logic eval error: assert failed. Details: app=55, pc=272" });

            var ex = await Assert.ThrowsAsync<AlgoSafeException>(() => AlgoSafeResourcePopulator.PopulateAsync(chain.Object, new List<Transaction> { call }, 1000));

            Assert.That(ex!.ErrorType, Is.EqualTo("SimulationFailed"));
            Assert.That(ex.Message, Does.Contain("assert failed"));
        }

        private static SimulateTransactionGroupResult Succeeded(int innerTransactions, SimulateUnnamedResourcesAccessed? accessed = null) => new()
        {
            TxnResults = new List<SimulateTransactionResult>
            {
                new()
                {
                    UnnamedResourcesAccessed = accessed,
                    ExecTrace = new SimulationTransactionExecTrace
                    {
                        InnerTrace = Enumerable.Range(0, innerTransactions).Select(_ => new SimulationTransactionExecTrace()).ToList()
                    }
                }
            }
        };
    }
}
