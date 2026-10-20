using System.Security.Claims;
using System.Text.Json;
using BiatecMCP.BusinessLogic;
using BiatecMCP.Model;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BiatecMCPTests
{
    /// <summary>Covers the Solana branches of the chain-family-agnostic MCP tools plus <c>createSolanaTransaction</c>.</summary>
    [TestFixture]
    public class SolanaMcpToolsTests
    {
        private const string Sender = "4nrWTvCH3vGghq2FXd22mifh3rKrKAXHw8xWJxt6tZ4H";
        private const string Receiver = "FoBNuzYQ4gpiP3jmevoiRVp3EnqBeXkB2HJ1LksSwYXJ";
        private const string Blockhash = "EkSnNWid2cvwEVnVx9aBqawnmiCNiDgp3gUdkDPTKN1N";

        private Mock<IBiatecWalletClient> _walletClient = null!;
        private DefaultHttpContext _httpContext = null!;
        private Mock<INetworkResolver> _networkResolver = null!;
        private Mock<ISolanaRpcDataSource> _solanaRpc = null!;
        private BiatecMCP.MCP.BiatecMCP _tool = null!;

        [SetUp]
        public void SetUp()
        {
            _walletClient = new Mock<IBiatecWalletClient>();
            _httpContext = new DefaultHttpContext();
            var accessor = Mock.Of<IHttpContextAccessor>(a => a.HttpContext == _httpContext);
            var chainRegistry = new Mock<IAlgorandChainRegistry>();
            var quoteProvider = new Mock<IDexQuoteProvider>();
            quoteProvider.Setup(p => p.ProviderName).Returns("BiatecRouter");
            _networkResolver = new Mock<INetworkResolver>();
            _networkResolver.Setup(r => r.ListNetworksAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new List<NetworkSummary>());
            _networkResolver
                .Setup(r => r.ResolveAsync("solana", It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ResolvedNetwork { Code = "solana", Family = ChainFamily.Sol, DisplayName = "Solana" });
            _solanaRpc = new Mock<ISolanaRpcDataSource>();

            _tool = new BiatecMCP.MCP.BiatecMCP(
                _walletClient.Object, accessor,
                new DexSwapAggregatorService(new[] { quoteProvider.Object }),
                Mock.Of<IAramidBridgeConfigProvider>(),
                chainRegistry.Object,
                _networkResolver.Object,
                Mock.Of<IPublicEvmRpcDataSource>(),
                Mock.Of<IPublicBitcoinDataSource>(),
                _solanaRpc.Object,
                NullLogger<BiatecMCP.MCP.BiatecMCP>.Instance);

            _httpContext.Request.Headers.Authorization = "Bearer tok";
            SetClaims(new Claim("primary_seed_address", "SEED"));
            _walletClient
                .Setup(c => c.GetAddressAsync("tok", "SEED", 0, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new DerivedAddressResponse { SolanaAddress = Sender });
        }

        private void SetClaims(params Claim[] claims) =>
            _httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));

        // ───────────── getCryptoAddress / getCryptoBalance ─────────────

        [Test]
        public async Task GetCryptoAddress_Solana_ReturnsDerivedSolanaAddress()
        {
            var result = await _tool.GetCryptoAddress("solana");

            Assert.That(result.Error, Is.Empty);
            Assert.That(result.Address, Is.EqualTo(Sender));
            Assert.That(result.Family, Is.EqualTo("Sol"));
        }

        [Test]
        public async Task GetCryptoAddress_Solana_NoAddressDerived_ReturnsClearError()
        {
            _walletClient
                .Setup(c => c.GetAddressAsync("tok", "SEED", 0, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new DerivedAddressResponse());

            var result = await _tool.GetCryptoAddress("solana");

            Assert.That(result.Address, Is.Empty);
            Assert.That(result.Error, Does.Contain("no Solana address"));
        }

        [Test]
        public async Task GetCryptoBalance_Solana_ReturnsSolAndLamports()
        {
            _solanaRpc.Setup(r => r.TryGetBalanceAsync(Sender, It.IsAny<CancellationToken>())).ReturnsAsync(2_500_000_000UL);

            var result = await _tool.GetCryptoBalance("solana");

            Assert.That(result.Error, Is.Empty);
            Assert.That(result.NativeCurrencySymbol, Is.EqualTo("SOL"));
            Assert.That(result.NativeBalance, Is.EqualTo(2.5m));
            Assert.That(result.NativeBalanceBaseUnits, Is.EqualTo("2500000000"));
        }

        [Test]
        public async Task GetCryptoBalance_Solana_ExplicitAddress_SkipsWalletLookup()
        {
            _solanaRpc.Setup(r => r.TryGetBalanceAsync(Receiver, It.IsAny<CancellationToken>())).ReturnsAsync(1UL);

            var result = await _tool.GetCryptoBalance("solana", address: Receiver);

            Assert.That(result.Address, Is.EqualTo(Receiver));
            _walletClient.Verify(c => c.GetAddressAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Test]
        public async Task GetCryptoBalance_Solana_RpcUnavailable_ReturnsRpcUnavailable()
        {
            _solanaRpc.Setup(r => r.TryGetBalanceAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync((ulong?)null);

            var result = await _tool.GetCryptoBalance("solana");

            Assert.That(result.ErrorType, Is.EqualTo("RpcUnavailable"));
        }

        // ───────────── createSolanaTransaction ─────────────

        [Test]
        public async Task CreateSolanaTransaction_HappyPath_BuildsTransferWireJson()
        {
            _solanaRpc.Setup(r => r.TryGetBalanceAsync(Sender, It.IsAny<CancellationToken>())).ReturnsAsync(10_000_000_000UL);
            _solanaRpc.Setup(r => r.TryGetLatestBlockhashAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Blockhash);

            var result = await _tool.CreateSolanaTransaction(Receiver, 1_000_000, "solana");

            Assert.That(result.Error, Is.Empty);
            Assert.That(result.Sender, Is.EqualTo(Sender));
            Assert.That(result.Receiver, Is.EqualTo(Receiver));
            Assert.That(result.AmountLamports, Is.EqualTo(1_000_000));
            Assert.That(result.EstimatedFeeLamports, Is.EqualTo(5000));
            var wire = JsonSerializer.Deserialize<SolanaUnsignedTransfer>(Convert.FromBase64String(result.UnsignedTransaction))!;
            Assert.That(wire.Recipient, Is.EqualTo(Receiver));
            Assert.That(wire.Lamports, Is.EqualTo(1_000_000UL));
            Assert.That(wire.RecentBlockhash, Is.EqualTo(Blockhash));
        }

        [Test]
        public async Task CreateSolanaTransaction_BalanceUnknown_StillBuilds()
        {
            _solanaRpc.Setup(r => r.TryGetBalanceAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync((ulong?)null);
            _solanaRpc.Setup(r => r.TryGetLatestBlockhashAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Blockhash);

            var result = await _tool.CreateSolanaTransaction(Receiver, 1_000_000, "solana");

            Assert.That(result.Error, Is.Empty);
            Assert.That(result.UnsignedTransaction, Is.Not.Empty);
        }

        [Test]
        public async Task CreateSolanaTransaction_InsufficientBalance_ReturnsInsufficientFundsWithoutFetchingBlockhash()
        {
            _solanaRpc.Setup(r => r.TryGetBalanceAsync(Sender, It.IsAny<CancellationToken>())).ReturnsAsync(1_004_999UL);

            var result = await _tool.CreateSolanaTransaction(Receiver, 1_000_000, "solana");

            Assert.That(result.ErrorType, Is.EqualTo("InsufficientFunds"));
            _solanaRpc.Verify(r => r.TryGetLatestBlockhashAsync(It.IsAny<CancellationToken>()), Times.Never);
        }

        [Test]
        public async Task CreateSolanaTransaction_BalanceExactlyCoversAmountAndFee_Builds()
        {
            _solanaRpc.Setup(r => r.TryGetBalanceAsync(Sender, It.IsAny<CancellationToken>())).ReturnsAsync(1_005_000UL);
            _solanaRpc.Setup(r => r.TryGetLatestBlockhashAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Blockhash);

            var result = await _tool.CreateSolanaTransaction(Receiver, 1_000_000, "solana");

            Assert.That(result.Error, Is.Empty);
        }

        [Test]
        public async Task CreateSolanaTransaction_BlockhashUnavailable_ReturnsRpcUnavailable()
        {
            _solanaRpc.Setup(r => r.TryGetBalanceAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(10_000_000_000UL);
            _solanaRpc.Setup(r => r.TryGetLatestBlockhashAsync(It.IsAny<CancellationToken>())).ReturnsAsync((string?)null);

            var result = await _tool.CreateSolanaTransaction(Receiver, 1_000_000, "solana");

            Assert.That(result.ErrorType, Is.EqualTo("RpcUnavailable"));
        }

        [Test]
        public async Task CreateSolanaTransaction_UnknownNetwork_ReturnsInvalidRequest()
        {
            _networkResolver.Setup(r => r.ResolveAsync("nope", It.IsAny<CancellationToken>())).ReturnsAsync((ResolvedNetwork?)null);

            var result = await _tool.CreateSolanaTransaction(Receiver, 1_000_000, "nope");

            Assert.That(result.ErrorType, Is.EqualTo("InvalidRequest"));
        }

        [Test]
        public async Task CreateSolanaTransaction_NonSolanaNetwork_ReturnsInvalidRequest()
        {
            _networkResolver
                .Setup(r => r.ResolveAsync("bitcoin", It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ResolvedNetwork { Family = ChainFamily.Btc, DisplayName = "Bitcoin" });

            var result = await _tool.CreateSolanaTransaction(Receiver, 1_000_000, "bitcoin");

            Assert.That(result.ErrorType, Is.EqualTo("InvalidRequest"));
            Assert.That(result.Error, Does.Contain("not a Solana network"));
        }

        [TestCase(0L)]
        [TestCase(-5L)]
        public async Task CreateSolanaTransaction_NonPositiveAmount_ReturnsInvalidRequest(long amount)
        {
            var result = await _tool.CreateSolanaTransaction(Receiver, amount, "solana");

            Assert.That(result.ErrorType, Is.EqualTo("InvalidRequest"));
            _solanaRpc.Verify(r => r.TryGetLatestBlockhashAsync(It.IsAny<CancellationToken>()), Times.Never);
        }

        [Test]
        public async Task CreateSolanaTransaction_InvalidReceiver_ReturnsInvalidRequestBeforeAnyRpcCall()
        {
            var result = await _tool.CreateSolanaTransaction("0xNotASolanaAddress", 1_000_000, "solana");

            Assert.That(result.ErrorType, Is.EqualTo("InvalidRequest"));
            Assert.That(result.Error, Does.Contain("not a valid Solana address"));
            _solanaRpc.Verify(r => r.TryGetBalanceAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Test]
        public async Task CreateSolanaTransaction_NoSolanaAddress_ReturnsNoSolanaAddress()
        {
            _walletClient
                .Setup(c => c.GetAddressAsync("tok", "SEED", 0, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new DerivedAddressResponse());

            var result = await _tool.CreateSolanaTransaction(Receiver, 1_000_000, "solana");

            Assert.That(result.ErrorType, Is.EqualTo("NoSolanaAddress"));
        }

        [Test]
        public async Task CreateSolanaTransaction_WalletApiError_IsSurfaced()
        {
            _walletClient
                .Setup(c => c.GetAddressAsync("tok", "SEED", 0, It.IsAny<CancellationToken>()))
                .ThrowsAsync(new WalletApiException(400, "seed_not_found", "No such seed."));

            // An explicit seedAddress is never second-guessed (only a claim-derived selector self-heals).
            var result = await _tool.CreateSolanaTransaction(Receiver, 1_000_000, "solana", seedAddress: "SEED");

            Assert.That(result.ErrorType, Is.EqualTo("seed_not_found"));
        }

        // ───────────── submitTransactionToBlockchain ─────────────

        [Test]
        public async Task SubmitTransactionToBlockchain_Solana_MissingSignClaim_ReturnsInsufficientScope()
        {
            var result = await _tool.SubmitTransactionToBlockchain(new List<string> { Convert.ToBase64String(new byte[] { 1 }) }, "solana");

            Assert.That(result.ErrorType, Is.EqualTo("InsufficientScope"));
            _solanaRpc.Verify(r => r.SendTransactionAsync(It.IsAny<byte[]>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Test]
        public async Task SubmitTransactionToBlockchain_Solana_BroadcastsAndReturnsSignatureAndExplorerLink()
        {
            SetClaims(new Claim("sign", "true"));
            _solanaRpc
                .Setup(r => r.SendTransactionAsync(It.Is<byte[]>(b => b.SequenceEqual(new byte[] { 1, 2, 3 })), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new SolanaSendResult("5sig", null));

            var result = await _tool.SubmitTransactionToBlockchain(new List<string> { Convert.ToBase64String(new byte[] { 1, 2, 3 }) }, "solana");

            Assert.That(result.Error, Is.Empty);
            Assert.That(result.TxId, Is.EqualTo("5sig"));
            Assert.That(result.ExplorerLink, Is.EqualTo("https://explorer.solana.com/tx/5sig"));
        }

        [Test]
        public async Task SubmitTransactionToBlockchain_Solana_NodeRejects_ReturnsBroadcastFailedWithNodeMessage()
        {
            SetClaims(new Claim("sign", "true"));
            _solanaRpc
                .Setup(r => r.SendTransactionAsync(It.IsAny<byte[]>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new SolanaSendResult(null, "Transaction simulation failed: Blockhash not found"));

            var result = await _tool.SubmitTransactionToBlockchain(new List<string> { Convert.ToBase64String(new byte[] { 1 }) }, "solana");

            Assert.That(result.ErrorType, Is.EqualTo("BroadcastFailed"));
            Assert.That(result.Error, Does.Contain("Blockhash not found"));
            Assert.That(result.TxId, Is.Empty);
        }

        [Test]
        public async Task SubmitTransactionToBlockchain_Solana_OutcomeUnknown_ToldNotToResendAndGivenTheSignatureToCheck()
        {
            SetClaims(new Claim("sign", "true"));
            _solanaRpc
                .Setup(r => r.SendTransactionAsync(It.IsAny<byte[]>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new SolanaSendResult(null, "The Solana RPC node did not answer cleanly, so it is UNKNOWN whether the transaction was accepted.", OutcomeUnknown: true, ExpectedSignature: "3abcSig"));

            var result = await _tool.SubmitTransactionToBlockchain(new List<string> { Convert.ToBase64String(new byte[] { 1 }) }, "solana");

            Assert.That(result.ErrorType, Is.EqualTo("BroadcastOutcomeUnknown"));
            Assert.That(result.Error, Does.Contain("UNKNOWN").And.Contain("https://explorer.solana.com/tx/3abcSig").And.Contain("Do NOT rebuild"));
            Assert.That(result.TxId, Is.Empty);
        }

        [Test]
        public async Task GetCryptoBalance_Solana_InvalidExplicitAddress_IsInvalidRequestNotAnOutageAndMakesNoRpcCall()
        {
            var result = await _tool.GetCryptoBalance("solana", address: "abc");

            Assert.That(result.ErrorType, Is.EqualTo("InvalidRequest"));
            _solanaRpc.Verify(r => r.TryGetBalanceAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Test]
        public async Task SubmitTransactionToBlockchain_Solana_MoreThanOneTransaction_ReturnsInvalidRequest()
        {
            SetClaims(new Claim("sign", "true"));
            var one = Convert.ToBase64String(new byte[] { 1 });

            var result = await _tool.SubmitTransactionToBlockchain(new List<string> { one, one }, "solana");

            Assert.That(result.ErrorType, Is.EqualTo("InvalidRequest"));
            _solanaRpc.Verify(r => r.SendTransactionAsync(It.IsAny<byte[]>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Test]
        public async Task SubmitTransactionToBlockchain_Solana_InvalidBase64_ReturnsInvalidRequest()
        {
            SetClaims(new Claim("sign", "true"));

            var result = await _tool.SubmitTransactionToBlockchain(new List<string> { "***not base64***" }, "solana");

            Assert.That(result.ErrorType, Is.EqualTo("InvalidRequest"));
        }

        // ───────────── signTransaction ─────────────

        [Test]
        public async Task SignTransaction_Solana_ForwardsPayloadToWalletApi()
        {
            SetClaims(new Claim("sign", "true"));
            var payload = new byte[] { 9, 9, 9 };
            _walletClient
                .Setup(c => c.SignAsync("tok", "solana", Sender, It.Is<IReadOnlyList<byte[]>>(l => l.Count == 1 && l[0].SequenceEqual(payload)), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new SignTransactionGroupResponse { SignedTransactions = new List<string> { "c2lnbmVk" } });

            var result = await _tool.SignTransaction(new List<string> { Convert.ToBase64String(payload) }, "solana", Sender);

            Assert.That(result.Error, Is.Empty);
            Assert.That(result.SignedTransactions, Is.EqualTo(new[] { "c2lnbmVk" }));
        }
    }
}
