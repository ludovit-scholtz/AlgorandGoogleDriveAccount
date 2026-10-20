using System.Security.Claims;
using Algorand;
using Algorand.Algod.Model;
using BiatecMCP.BusinessLogic;
using BiatecMCP.BusinessLogic.AlgoSafe;
using BiatecMCP.Model;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BiatecMCPTests
{
    /// <summary>The AlgoSafe tools' wiring: network resolution, sender resolution, error mapping, and the chain-state reader's guards (no node needed).</summary>
    [TestFixture]
    public class AlgoSafeMcpToolsTests
    {
        private static readonly string SenderAddress = Address.ForApplication(1).EncodeAsString();

        private Mock<IBiatecWalletClient> _walletClient = null!;
        private Mock<INetworkResolver> _networkResolver = null!;
        private Mock<IAlgoSafeChainClientFactory> _chains = null!;
        private Mock<IAlgoSafeChainClient> _chain = null!;
        private BiatecMCP.MCP.BiatecMCP _tool = null!;

        [SetUp]
        public void SetUp()
        {
            _walletClient = new Mock<IBiatecWalletClient>();
            var httpContext = new DefaultHttpContext();
            httpContext.Request.Headers.Authorization = "Bearer tok";
            httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("primary_seed_address", "SEED") }, "test"));
            var accessor = Mock.Of<IHttpContextAccessor>(a => a.HttpContext == httpContext);

            _networkResolver = new Mock<INetworkResolver>();
            _networkResolver.Setup(r => r.ListNetworksAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new List<NetworkSummary> { new() { Code = "algorand-testnet" } });
            _networkResolver
                .Setup(r => r.ResolveAsync("algorand-testnet", It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ResolvedNetwork { Code = "algorand-testnet", Family = ChainFamily.Avm, AvmChain = new AlgorandChain { GenesisId = "testnet-v1.0" } });
            _networkResolver
                .Setup(r => r.ResolveAsync("bitcoin", It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ResolvedNetwork { Code = "bitcoin", Family = ChainFamily.Btc });

            _chain = new Mock<IAlgoSafeChainClient>();
            _chains = new Mock<IAlgoSafeChainClientFactory>();
            _chains.Setup(f => f.Create(It.IsAny<AlgorandChain>())).Returns(_chain.Object);

            _walletClient
                .Setup(c => c.GetAddressAsync("tok", "SEED", 0, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new DerivedAddressResponse { Address = SenderAddress });

            _tool = new BiatecMCP.MCP.BiatecMCP(
                _walletClient.Object, accessor,
                new DexSwapAggregatorService(Array.Empty<IDexQuoteProvider>()),
                Mock.Of<IAramidBridgeConfigProvider>(),
                Mock.Of<IAlgorandChainRegistry>(),
                _networkResolver.Object,
                Mock.Of<IPublicEvmRpcDataSource>(),
                Mock.Of<IPublicBitcoinDataSource>(),
                Mock.Of<ISolanaRpcDataSource>(),
                _chains.Object,
                NullLogger<BiatecMCP.MCP.BiatecMCP>.Instance);
        }

        [Test]
        public async Task GetAlgoSafe_NonAlgorandNetwork_IsRejectedListingTheValidCodes()
        {
            var result = await _tool.GetAlgoSafe(1, "bitcoin");

            Assert.That(result.Safe, Is.Null);
            Assert.That(result.ErrorType, Is.EqualTo("InvalidRequest"));
            Assert.That(result.Error, Does.Contain("algorand-testnet"));
            _chains.Verify(f => f.Create(It.IsAny<AlgorandChain>()), Times.Never);
        }

        [Test]
        public async Task GetAlgoSafe_ApplicationThatIsNotAnAlgoSafe_ReturnsUnsupportedWithoutDecoding()
        {
            _chain.Setup(c => c.GetApplicationAsync(7, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new Application { Id = 7, Params = new ApplicationParams { ApprovalProgram = new TEALProgram(new byte[] { 1, 2, 3 }), Creator = Address.ForApplication(2) } });

            var result = await _tool.GetAlgoSafe(7, "algorand-testnet");

            Assert.That(result.Error, Is.Empty);
            Assert.That(result.Safe!.Supported, Is.False);
            Assert.That(result.Safe.ContractVersion, Is.Null);
        }

        [Test]
        public async Task GetAlgoSafe_MissingApplication_ReportsAppNotFound()
        {
            _chain.Setup(c => c.GetApplicationAsync(8, It.IsAny<CancellationToken>())).ReturnsAsync((Application?)null);

            var result = await _tool.GetAlgoSafe(8, "algorand-testnet");

            Assert.That(result.ErrorType, Is.EqualTo("AppNotFound"));
        }

        [Test]
        public async Task ProposeAlgoSafeTransaction_AgainstANonAlgoSafeApp_FailsWithUnsupportedContract()
        {
            _chain.Setup(c => c.GetApplicationAsync(7, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new Application { Id = 7, Params = new ApplicationParams { ApprovalProgram = new TEALProgram(new byte[] { 1, 2, 3 }) } });

            var result = await _tool.ProposeAlgoSafeTransaction(
                7, 1, new List<BiatecMCP.MCP.BiatecMCP.AlgoSafeTransferInput> { new() { Receiver = SenderAddress, Amount = 1 } }, "algorand-testnet");

            Assert.That(result.ErrorType, Is.EqualTo("UnsupportedContract"));
            Assert.That(result.UnsignedTransactions, Is.Empty);
        }

        [Test]
        public async Task ProposeAlgoSafeTransaction_WithoutTransfers_IsRejected()
        {
            var result = await _tool.ProposeAlgoSafeTransaction(7, 1, new List<BiatecMCP.MCP.BiatecMCP.AlgoSafeTransferInput>(), "algorand-testnet");

            Assert.That(result.ErrorType, Is.EqualTo("InvalidRequest"));
        }

        [Test]
        public async Task CreateAlgoSafe_NoAddressAvailable_ReportsNoAlgorandAddress()
        {
            _walletClient
                .Setup(c => c.GetAddressAsync("tok", "SEED", 0, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new DerivedAddressResponse());

            var result = await _tool.CreateAlgoSafe("Treasury", "algorand-testnet", validatorAppId: 5);

            Assert.That(result.ErrorType, Is.EqualTo("NoAlgorandAddress"));
        }

        [Test]
        public async Task CreateAlgoSafe_EmptyName_IsRejected()
        {
            _chain.Setup(c => c.GetTransactionParamsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new TransactionParametersResponse());

            var result = await _tool.CreateAlgoSafe(" ", "algorand-testnet", validatorAppId: 5);

            Assert.That(result.ErrorType, Is.EqualTo("InvalidRequest"));
        }

        [Test]
        public async Task GetAlgoSafeDeployment_UnknownTransaction_ReportsNotFound()
        {
            _chain.Setup(c => c.GetPendingTransactionAsync("TX", It.IsAny<CancellationToken>())).ReturnsAsync((Algorand.Algod.Model.Transactions.IReturnableTransaction?)null);

            var result = await _tool.GetAlgoSafeDeployment("TX", "algorand-testnet");

            Assert.That(result.ErrorType, Is.EqualTo("TransactionNotFound"));
        }

        [Test]
        public async Task GetAlgoSafeDeployment_BlankTransactionId_IsRejected()
        {
            var result = await _tool.GetAlgoSafeDeployment(" ", "algorand-testnet");

            Assert.That(result.ErrorType, Is.EqualTo("InvalidRequest"));
        }

        [Test]
        public void SupportedContractVersions_PinTheKnownReleases()
        {
            Assert.That(AlgoSafeConstants.SupportedContractVersions[AlgoSafeConstants.LatestApprovalSha256Hex], Is.EqualTo("v3.4.0"));
            Assert.That(AlgoSafeConstants.SupportedContractVersions, Has.Count.EqualTo(5));
        }
    }
}
