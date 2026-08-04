using System.Security.Claims;
using Algorand;
using Algorand.Algod.Model;
using Algorand.Algod.Model.Transactions;
using BiatecMCP.BusinessLogic;
using BiatecMCP.Helper;
using BiatecMCP.Model;
using BiatecSelfCustodyCore.BusinessLogic;
using BiatecSelfCustodyCore.Repository;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BiatecMCPTests
{
    /// <summary>
    /// Covers the multisig MCP tools end to end: <c>getMultisigAddress</c> (deriving the multisig account's
    /// address from an ordered participant list + threshold), <c>convertToMultisigTransactions</c> (wrapping
    /// standard unsigned transactions into the multisig envelopes each cosigner independently signs), the
    /// <c>signTransaction</c> participant check (a multisig envelope can only be cosigned by one of its own
    /// participants), and - via the real <see cref="DriveService"/> multisig branch with only the storage
    /// repository mocked - actually cosigning and merging a 2-of-3 envelope built from real ARC-76 slot
    /// 0/1/2 accounts.
    /// </summary>
    [TestFixture]
    public class MultisigMcpToolsTests
    {
        private const string TestEmail = "user@example.com";
        private const string TestProvider = "Fake";

        private Mock<IBiatecWalletClient> _walletClient = null!;
        private IHttpContextAccessor _httpContextAccessor = null!;
        private DefaultHttpContext _httpContext = null!;
        private Mock<IDexQuoteProvider> _biatecRouterQuoteProvider = null!;
        private Mock<IAramidBridgeConfigProvider> _aramidBridgeConfigProvider = null!;
        private Mock<IAlgorandChainRegistry> _chainRegistry = null!;
        private Mock<INetworkResolver> _networkResolver = null!;
        private Mock<IPublicEvmRpcDataSource> _evmRpcDataSource = null!;
        private Mock<IPublicBitcoinDataSource> _bitcoinDataSource = null!;

        [SetUp]
        public void SetUp()
        {
            _walletClient = new Mock<IBiatecWalletClient>();
            _httpContext = new DefaultHttpContext();
            _httpContextAccessor = Mock.Of<IHttpContextAccessor>(a => a.HttpContext == _httpContext);
            _biatecRouterQuoteProvider = new Mock<IDexQuoteProvider>();
            _biatecRouterQuoteProvider.Setup(p => p.ProviderName).Returns("BiatecRouter");
            _aramidBridgeConfigProvider = new Mock<IAramidBridgeConfigProvider>();
            _chainRegistry = new Mock<IAlgorandChainRegistry>();
            _networkResolver = new Mock<INetworkResolver>();
            _evmRpcDataSource = new Mock<IPublicEvmRpcDataSource>();
            _bitcoinDataSource = new Mock<IPublicBitcoinDataSource>();
        }

        private BiatecMCP.MCP.BiatecMCP CreateTool() =>
            new(_walletClient.Object, _httpContextAccessor,
                new DexSwapAggregatorService(new[] { _biatecRouterQuoteProvider.Object }),
                _aramidBridgeConfigProvider.Object,
                _chainRegistry.Object,
                _networkResolver.Object,
                _evmRpcDataSource.Object,
                _bitcoinDataSource.Object,
                NullLogger<BiatecMCP.MCP.BiatecMCP>.Instance);

        private static TransactionParametersResponse SuggestedParams() => new()
        {
            Fee = 0,
            MinFee = 1000,
            GenesisHash = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray(),
            GenesisId = "testnet-v1.0",
            LastRound = 5_000_000,
            ConsensusVersion = "https://github.com/algorandfoundation/specs/tree/abc123"
        };

        /// <summary>
        /// The user-story fixture: the same seed's ARC-76 slot 0, 1, and 2 accounts, with the participant
        /// list sorted alphabetically by address (order is part of the multisig account's identity).
        /// </summary>
        private static (string mnemonic, List<Account> accounts, List<string> participants) BuildSlot012Participants()
        {
            var mnemonic = new Account().ToMnemonic();
            var accounts = new[] { 0, 1, 2 }
                .Select(slot => ARC76Account.Algorand.ARC76.GetEmailAccount(TestEmail, mnemonic, slot))
                .ToList();
            var participants = accounts
                .Select(a => a.Address.EncodeAsString())
                .OrderBy(a => a, StringComparer.Ordinal)
                .ToList();
            return (mnemonic, accounts, participants);
        }

        [Test]
        public async Task GetMultisigAddress_TwoOfThreeSlotAccounts_DerivesTheSameAddressAsTheSdk()
        {
            var (_, _, participants) = BuildSlot012Participants();

            var result = await CreateTool().GetMultisigAddress(2, participants);

            Assert.That(result.Error, Is.Empty);
            Assert.That(result.Version, Is.EqualTo(1));
            Assert.That(result.Threshold, Is.EqualTo(2));
            Assert.That(result.ParticipantAddresses, Is.EqualTo(participants));

            var expected = new MultisigAddress(1, 2, participants.Select(a => new Address(a).Bytes).ToList())
                .ToAddress().EncodeAsString();
            Assert.That(result.MultisigAddress, Is.EqualTo(expected));
        }

        [Test]
        public async Task GetMultisigAddress_DifferentParticipantOrder_DerivesADifferentAddress()
        {
            var (_, _, participants) = BuildSlot012Participants();
            var reversed = participants.AsEnumerable().Reverse().ToList();

            var sorted = await CreateTool().GetMultisigAddress(2, participants);
            var unsorted = await CreateTool().GetMultisigAddress(2, reversed);

            Assert.That(sorted.MultisigAddress, Is.Not.EqualTo(unsorted.MultisigAddress));
        }

        [Test]
        public async Task GetMultisigAddress_ThresholdAboveParticipantCount_FailsWithInvalidRequest()
        {
            var (_, _, participants) = BuildSlot012Participants();

            var result = await CreateTool().GetMultisigAddress(4, participants);

            Assert.That(result.MultisigAddress, Is.Empty);
            Assert.That(result.ErrorType, Is.EqualTo("InvalidRequest"));
        }

        [Test]
        public async Task ConvertToMultisigTransactions_SelfPayOneAlgo_ProducesAValidEnvelopeAndTheEnvelopeCosignsAndMerges()
        {
            // 2-of-3 multisig over the seed's slot 0/1/2 accounts, participants sorted alphabetically.
            var (mnemonic, _, participants) = BuildSlot012Participants();
            var tool = CreateTool();
            var addressResult = await tool.GetMultisigAddress(2, participants);
            Assert.That(addressResult.Error, Is.Empty);
            var multisigAddress = addressResult.MultisigAddress;

            // 1 ALGO self-payment from the multisig account to itself.
            var sender = new Address(multisigAddress);
            var unsigned = AlgorandTransactionBuilder.BuildPayment(sender, sender, 1_000_000, string.Empty, SuggestedParams());

            var convertResult = await tool.ConvertToMultisigTransactions(
                new List<string> { Convert.ToBase64String(unsigned) }, 2, participants);

            Assert.That(convertResult.Error, Is.Empty);
            Assert.That(convertResult.MultisigAddress, Is.EqualTo(multisigAddress));
            Assert.That(convertResult.UnsignedTransactionEnvelopes, Has.Count.EqualTo(1));

            // Parse the produced bytes with the Algorand4 SDK and check the signature object is correct:
            // an unsigned multisig envelope is a SignedTransaction whose MSig names every participant (in
            // order) with no signature contributed yet, wrapping the untouched inner payment.
            var envelope = SignedTransaction.FromBase64String(convertResult.UnsignedTransactionEnvelopes[0]);
            Assert.That(envelope.MSig, Is.Not.Null);
            Assert.That(envelope.MSig.Version, Is.EqualTo(1));
            Assert.That(envelope.MSig.Threshold, Is.EqualTo(2));
            Assert.That(envelope.MSig.Subsigs, Has.Count.EqualTo(3));
            for (var i = 0; i < participants.Count; i++)
            {
                Assert.That(new Address(envelope.MSig.Subsigs[i].key.GetEncoded()).EncodeAsString(), Is.EqualTo(participants[i]));
                // An unsigned subsig round-trips through msgpack as either null or a blank (all-zero)
                // Signature, depending on decoder defaults - both mean "no signature contributed yet".
                Assert.That(envelope.MSig.Subsigs[i].sig, Is.Null.Or.EqualTo(new Signature()));
            }
            Assert.That(envelope.Sig, Is.Null.Or.EqualTo(new Signature()));

            var payment = (PaymentTransaction)envelope.Tx;
            Assert.That(payment.Sender.EncodeAsString(), Is.EqualTo(multisigAddress));
            Assert.That(payment.Receiver.EncodeAsString(), Is.EqualTo(multisigAddress));
            Assert.That(payment.Amount, Is.EqualTo(1_000_000UL));

            // Signing: two participants (slot 0 and slot 1) each cosign their own fresh copy of the same
            // envelope through the real DriveService multisig branch (only the storage repository is
            // mocked - real ARC-76 derivation, real ed25519 signing), then the copies merge into one
            // broadcastable transaction whose multisig signature actually verifies.
            var accountRepository = new Mock<ICloudAccountRepository>();
            accountRepository
                .Setup(r => r.LoadAccountAsync(TestEmail, It.IsAny<int>(), TestProvider, It.IsAny<string?>(), It.IsAny<string?>()))
                .ReturnsAsync((string email, int slot, string _, string? _, string? _) =>
                    ARC76Account.Algorand.ARC76.GetEmailAccount(email, mnemonic, slot));
            var driveService = new DriveService(accountRepository.Object, new Mock<ILogger<DriveService>>().Object);

            var envelopeBytes = Convert.FromBase64String(convertResult.UnsignedTransactionEnvelopes[0]);
            var signedBySlot0 = await driveService.SignTransactionAsync(TestEmail, envelopeBytes, TestProvider, slot: 0);
            var signedBySlot1 = await driveService.SignTransactionAsync(TestEmail, envelopeBytes, TestProvider, slot: 1);

            var mergedBase64 = MultisigTransactionBuilder.Merge(new[]
            {
                Convert.ToBase64String(signedBySlot0),
                Convert.ToBase64String(signedBySlot1)
            });

            var merged = SignedTransaction.FromBase64String(mergedBase64);
            var blankSignature = new Signature();
            Assert.That(merged.MSig.Subsigs.Count(s => s.sig != null && !s.sig.Equals(blankSignature)), Is.EqualTo(2));
            Assert.That(merged.MSig.Verify(merged.Tx.BytesToSign()), Is.True);
        }

        [Test]
        public async Task ConvertToMultisigTransactions_SenderIsNotTheDerivedMultisigAddress_FailsWithoutConverting()
        {
            var (_, accounts, participants) = BuildSlot012Participants();

            // Built from a participant's own address, not the multisig account's.
            var wrongSender = accounts[0].Address;
            var unsigned = AlgorandTransactionBuilder.BuildPayment(wrongSender, wrongSender, 1_000_000, string.Empty, SuggestedParams());

            var result = await CreateTool().ConvertToMultisigTransactions(
                new List<string> { Convert.ToBase64String(unsigned) }, 2, participants);

            Assert.That(result.UnsignedTransactionEnvelopes, Is.Empty);
            Assert.That(result.ErrorType, Is.EqualTo("InvalidRequest"));
            Assert.That(result.Error, Does.Contain(wrongSender.EncodeAsString()));
        }

        [Test]
        public async Task ConvertToMultisigTransactions_AlreadyAMultisigEnvelope_FailsWithClearError()
        {
            var (_, _, participants) = BuildSlot012Participants();
            var tool = CreateTool();
            var multisigAddress = (await tool.GetMultisigAddress(2, participants)).MultisigAddress;
            var sender = new Address(multisigAddress);
            var unsigned = AlgorandTransactionBuilder.BuildPayment(sender, sender, 1_000_000, string.Empty, SuggestedParams());
            var envelope = (await tool.ConvertToMultisigTransactions(
                new List<string> { Convert.ToBase64String(unsigned) }, 2, participants)).UnsignedTransactionEnvelopes[0];

            var result = await tool.ConvertToMultisigTransactions(new List<string> { envelope }, 2, participants);

            Assert.That(result.UnsignedTransactionEnvelopes, Is.Empty);
            Assert.That(result.ErrorType, Is.EqualTo("InvalidRequest"));
            Assert.That(result.Error, Does.Contain("already a multisig envelope"));
        }

        // ───────────────────────── signTransaction's multisig participant check ─────────────────────────

        private void SetUpSignableAvmNetwork() =>
            _networkResolver
                .Setup(r => r.ResolveAsync("algorand-testnet", It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ResolvedNetwork { Code = "algorand-testnet", Family = ChainFamily.Avm, DisplayName = "Algorand Testnet" });

        private async Task<(string envelopeBase64, List<string> participants)> BuildEnvelopeForSignTests()
        {
            var (_, _, participants) = BuildSlot012Participants();
            var tool = CreateTool();
            var multisigAddress = (await tool.GetMultisigAddress(2, participants)).MultisigAddress;
            var sender = new Address(multisigAddress);
            var unsigned = AlgorandTransactionBuilder.BuildPayment(sender, sender, 1_000_000, string.Empty, SuggestedParams());
            var convertResult = await tool.ConvertToMultisigTransactions(
                new List<string> { Convert.ToBase64String(unsigned) }, 2, participants);
            return (convertResult.UnsignedTransactionEnvelopes[0], participants);
        }

        [Test]
        public async Task SignTransaction_MultisigEnvelopeWithNonParticipantAddress_FailsBeforeCallingTheWalletApi()
        {
            SetUpSignableAvmNetwork();
            _httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("sign", "true") }, "test"));
            _httpContext.Request.Headers.Authorization = "Bearer tok";
            var (envelope, participants) = await BuildEnvelopeForSignTests();
            var outsider = new Account().Address.EncodeAsString();

            var result = await CreateTool().SignTransaction(new List<string> { envelope }, "algorand-testnet", outsider);

            Assert.That(result.SignedTransactions, Is.Empty);
            Assert.That(result.ErrorType, Is.EqualTo("InvalidRequest"));
            Assert.That(result.Error, Does.Contain(outsider).And.Contain(participants[0]));
            _walletClient.Verify(
                c => c.SignAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<byte[]>>(), It.IsAny<CancellationToken>()),
                Times.Never);
        }

        [Test]
        public async Task SignTransaction_MultisigEnvelopeWithParticipantAddress_ForwardsToTheWalletApi()
        {
            SetUpSignableAvmNetwork();
            _httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("sign", "true") }, "test"));
            _httpContext.Request.Headers.Authorization = "Bearer tok";
            var (envelope, participants) = await BuildEnvelopeForSignTests();
            _walletClient
                .Setup(c => c.SignAsync("tok", "algorand-testnet", participants[0], It.IsAny<IReadOnlyList<byte[]>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new SignTransactionGroupResponse { SignedTransactions = new List<string> { "c2lnbmVk" } });

            var result = await CreateTool().SignTransaction(new List<string> { envelope }, "algorand-testnet", participants[0]);

            Assert.That(result.Error, Is.Empty);
            Assert.That(result.SignedTransactions, Has.Count.EqualTo(1));
        }
    }
}
