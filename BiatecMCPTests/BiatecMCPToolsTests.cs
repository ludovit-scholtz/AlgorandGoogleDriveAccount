using System.Security.Claims;
using BiatecMCP.BusinessLogic;
using BiatecMCP.Model;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace BiatecMCPTests
{
    /// <summary>
    /// Tests for <see cref="BiatecMCP.MCP.BiatecMCP"/> that don't require a live Algod node or BiatecOIDC -
    /// the claim-reading/scope-gating logic that runs before any network I/O. The full build path (which
    /// talks to a real Algod node via <c>HttpClientConfigurator</c>) is covered independently by
    /// <see cref="AlgorandTransactionBuilderTests"/>/<see cref="MultisigTransactionBuilderTests"/>
    /// (transaction construction) and <see cref="BiatecWalletClientTests"/> (the BiatecOIDC wallet API call)
    /// - and, per this rewrite's verification plan, by a manual end-to-end run against a real MCP client,
    /// Algod, and BiatecOIDC.
    /// </summary>
    [TestFixture]
    public class BiatecMCPToolsTests
    {
        private Mock<IBiatecWalletClient> _walletClient = null!;
        private IHttpContextAccessor _httpContextAccessor = null!;
        private DefaultHttpContext _httpContext = null!;
        private IOptionsMonitor<AlgodConfiguration> _algodConfig = null!;
        private Mock<IDexQuoteProvider> _biatecRouterQuoteProvider = null!;
        private Mock<IAramidBridgeConfigProvider> _aramidBridgeConfigProvider = null!;
        private Mock<IAlgorandChainRegistry> _chainRegistry = null!;

        [SetUp]
        public void SetUp()
        {
            _walletClient = new Mock<IBiatecWalletClient>();
            _httpContext = new DefaultHttpContext();
            _httpContextAccessor = Mock.Of<IHttpContextAccessor>(a => a.HttpContext == _httpContext);
            _algodConfig = Mock.Of<IOptionsMonitor<AlgodConfiguration>>(m => m.CurrentValue == new AlgodConfiguration());
            _biatecRouterQuoteProvider = new Mock<IDexQuoteProvider>();
            _biatecRouterQuoteProvider.Setup(p => p.ProviderName).Returns("BiatecRouter");
            _aramidBridgeConfigProvider = new Mock<IAramidBridgeConfigProvider>();
            _chainRegistry = new Mock<IAlgorandChainRegistry>();
            _chainRegistry.Setup(r => r.TryGetChainAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync((AlgorandChain?)null);
            _chainRegistry.Setup(r => r.TryGetChainByAramidIdAsync(It.IsAny<long>(), It.IsAny<CancellationToken>())).ReturnsAsync((AlgorandChain?)null);
        }

        private BiatecMCP.MCP.BiatecMCP CreateTool(DexSwapAggregatorService? aggregator = null) =>
            new(_walletClient.Object, _httpContextAccessor, _algodConfig,
                aggregator ?? new DexSwapAggregatorService(new[] { _biatecRouterQuoteProvider.Object }),
                _aramidBridgeConfigProvider.Object,
                _chainRegistry.Object,
                NullLogger<BiatecMCP.MCP.BiatecMCP>.Instance);

        private void SetBearerToken(string token) =>
            _httpContext.Request.Headers.Authorization = $"Bearer {token}";

        private void SetClaims(params Claim[] claims) =>
            _httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));

        [Test]
        public async Task GetAccountAddress_AlgorandAddressClaimPresent_ReturnsItWithoutCallingWalletClient()
        {
            SetClaims(new Claim("algorand_address", "SOMEADDRESS"));

            var result = await CreateTool().GetAccountAddress();

            Assert.That(result.Address, Is.EqualTo("SOMEADDRESS"));
            Assert.That(result.Error, Is.Empty);
            _walletClient.Verify(c => c.ListSeedsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Test]
        public async Task GetAccountAddress_NoClaim_FallsBackToPrimarySeedFromWalletApi()
        {
            SetClaims(); // no algorand_address claim
            SetBearerToken("tok");
            _walletClient
                .Setup(c => c.ListSeedsAsync("tok", It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ListSeedsResponse
                {
                    Seeds =
                    {
                        new SeedResponse { Address = "OLD", IsPrimary = false },
                        new SeedResponse { Address = "PRIMARY", IsPrimary = true }
                    }
                });

            var result = await CreateTool().GetAccountAddress();

            Assert.That(result.Address, Is.EqualTo("PRIMARY"));
        }

        [Test]
        public async Task GetAccountAddress_NoClaimAndNoSeeds_ReturnsClearError()
        {
            SetClaims();
            SetBearerToken("tok");
            _walletClient.Setup(c => c.ListSeedsAsync("tok", It.IsAny<CancellationToken>())).ReturnsAsync(new ListSeedsResponse());

            var result = await CreateTool().GetAccountAddress();

            Assert.That(result.Address, Is.Empty);
            Assert.That(result.Error, Does.Contain("no Algorand address"));
        }

        [Test]
        public async Task GetAccountAddress_NoClaimAndNoBearerToken_ReturnsUnauthorizedMessage()
        {
            SetClaims();
            // no bearer token set at all

            var result = await CreateTool().GetAccountAddress();

            Assert.That(result.Error, Does.Contain("bearer token"));
        }

        // ───────────────────────── Multi-address (ARC-76 slot) support ─────────────────────────

        [Test]
        public async Task GetAccountAddress_DefaultSlotAndNoPrimaryAddress_UsesClaimFastPath()
        {
            SetClaims(new Claim("algorand_address", "SOMEADDRESS"));

            var result = await CreateTool().GetAccountAddress();

            Assert.That(result.Address, Is.EqualTo("SOMEADDRESS"));
            _walletClient.Verify(c => c.GetAddressAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Test]
        public async Task GetAccountAddress_NonZeroSlot_DerivesFromPrimarySeedViaWalletApi()
        {
            SetClaims(new Claim("algorand_address", "SOMEADDRESS")); // claim present but ignored - slot != 0
            SetBearerToken("tok");
            _walletClient
                .Setup(c => c.ListSeedsAsync("tok", It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ListSeedsResponse { Seeds = { new SeedResponse { Address = "PRIMARY", IsPrimary = true } } });
            _walletClient
                .Setup(c => c.GetAddressAsync("tok", "PRIMARY", 1, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new DerivedAddressResponse { Address = "SECOND-ADDRESS", PrimaryAddress = "PRIMARY", Slot = 1 });

            var result = await CreateTool().GetAccountAddress(slot: 1);

            Assert.That(result.Address, Is.EqualTo("SECOND-ADDRESS"));
        }

        [Test]
        public async Task GetAccountAddress_ExplicitPrimaryAddress_DerivesFromThatSeedViaWalletApi()
        {
            SetClaims();
            SetBearerToken("tok");
            _walletClient
                .Setup(c => c.GetAddressAsync("tok", "OTHER-SEED", 0, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new DerivedAddressResponse { Address = "OTHER-DERIVED", PrimaryAddress = "OTHER-SEED", Slot = 0 });

            var result = await CreateTool().GetAccountAddress(primaryAddress: "OTHER-SEED");

            Assert.That(result.Address, Is.EqualTo("OTHER-DERIVED"));
            _walletClient.Verify(c => c.ListSeedsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Test]
        public async Task ListAlgorandAddresses_ReturnsAddressesFromWalletClient()
        {
            SetBearerToken("tok");
            _walletClient
                .Setup(c => c.ListAddressesAsync("tok", It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ListAddressesResponse
                {
                    Addresses = { new AddressResponse { Address = "A1", IsPrimary = true }, new AddressResponse { Address = "A2", IsPrimary = false } }
                });

            var result = await CreateTool().ListAlgorandAddresses();

            Assert.That(result.Addresses, Has.Count.EqualTo(2));
            Assert.That(result.Error, Is.Empty);
        }

        [Test]
        public async Task ListAlgorandAddresses_NoBearerToken_ReturnsError()
        {
            var result = await CreateTool().ListAlgorandAddresses();

            Assert.That(result.Error, Does.Contain("bearer token"));
        }

        // ───────────────────────── createPaymentTransaction / createOptInTransaction (no sign claim needed - build only) ─────────────────────────

        [Test]
        public async Task CreatePaymentTransaction_NoBearerTokenAndNoClaim_ReturnsUnauthorized()
        {
            SetClaims(); // no algorand_address claim, no bearer token at all

            var result = await CreateTool().CreatePaymentTransaction(receiverAccount: "SOME", amount: 1);

            Assert.That(result.Error, Does.Contain("bearer token"));
            Assert.That(result.UnsignedTransaction, Is.Empty);
        }

        [Test]
        public async Task CreatePaymentTransaction_WithPrimaryAddressAndSlot_ResolvesSenderViaWalletApi()
        {
            SetClaims();
            SetBearerToken("tok");
            _walletClient
                .Setup(c => c.GetAddressAsync("tok", "OTHER-SEED", 10, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new DerivedAddressResponse { Address = "OTHER-SEED", PrimaryAddress = "OTHER-SEED", Slot = 10 });

            // No Algod network is configured for "mainnet-v1.0" in this test's AlgodConfiguration, so the
            // call fails deterministically once it reaches that stage - proving the sender was resolved
            // through the wallet API first (an unconfigured genesisId throws ArgumentException).
            var result = await CreateTool().CreatePaymentTransaction(receiverAccount: "SOME", amount: 1, primaryAddress: "OTHER-SEED", slot: 10);

            _walletClient.Verify(c => c.GetAddressAsync("tok", "OTHER-SEED", 10, It.IsAny<CancellationToken>()), Times.Once);
            Assert.That(result.ErrorType, Does.Contain("ArgumentException"));
            // Never touches the wallet's sign endpoint - this tool only builds, never signs.
            _walletClient.Verify(c => c.SignAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<byte[]>>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Test]
        public async Task CreateOptInTransaction_ZeroAssetId_ReturnsInvalidRequest()
        {
            SetClaims(new Claim("algorand_address", "SOMEADDRESS"));

            var result = await CreateTool().CreateOptInTransaction(assetId: 0);

            Assert.That(result.ErrorType, Is.EqualTo("InvalidRequest"));
        }

        [Test]
        public async Task CreateOptInTransaction_NoAlgorandAddress_ReturnsClearError()
        {
            SetClaims();
            SetBearerToken("tok");
            _walletClient.Setup(c => c.ListSeedsAsync("tok", It.IsAny<CancellationToken>())).ReturnsAsync(new ListSeedsResponse());

            var result = await CreateTool().CreateOptInTransaction(assetId: 123);

            Assert.That(result.ErrorType, Is.EqualTo("NoAlgorandAddress"));
        }

        // ───────────────────────── createAssetCreateTransaction ─────────────────────────

        [Test]
        public async Task CreateAssetCreateTransaction_NoBearerTokenAndNoClaim_ReturnsUnauthorized()
        {
            SetClaims();

            var result = await CreateTool().CreateAssetCreateTransaction(total: 1000, decimals: 0, unitName: "T", assetName: "Test");

            Assert.That(result.Error, Does.Contain("bearer token"));
        }

        // ───────────────────────── signTransaction (standalone - requires 'sign' claim) ─────────────────────────

        [Test]
        public async Task SignTransaction_MissingSignClaim_ReturnsInsufficientScope_WithoutCallingWalletClient()
        {
            SetClaims();
            SetBearerToken("tok");

            var result = await CreateTool().SignTransaction(new List<string> { "AA==" });

            Assert.That(result.ErrorType, Is.EqualTo("InsufficientScope"));
            _walletClient.Verify(c => c.SignAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<byte[]>>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Test]
        public async Task SignTransaction_EmptyList_ReturnsInvalidRequest()
        {
            SetClaims(new Claim("sign", "true"));
            SetBearerToken("tok");

            var result = await CreateTool().SignTransaction(new List<string>());

            Assert.That(result.ErrorType, Is.EqualTo("InvalidRequest"));
        }

        [Test]
        public async Task SignTransaction_SignClaimPresentButNoBearerToken_ReturnsUnauthorized()
        {
            SetClaims(new Claim("sign", "true"));

            var result = await CreateTool().SignTransaction(new List<string> { "AA==" });

            Assert.That(result.ErrorType, Is.EqualTo("Unauthorized"));
        }

        [Test]
        public async Task SignTransaction_ValidRequest_ForwardsToWalletClientWithPrimaryAddressAndSlot()
        {
            SetClaims(new Claim("sign", "true"));
            SetBearerToken("tok");
            _walletClient
                .Setup(c => c.SignAsync("tok", It.IsAny<IReadOnlyList<byte[]>>(), "SEED", 5, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new SignTransactionGroupResponse { SignedTransactions = { "c2lnbmVk" } });

            var result = await CreateTool().SignTransaction(new List<string> { Convert.ToBase64String(new byte[] { 1, 2, 3 }) }, primaryAddress: "SEED", slot: 5);

            Assert.That(result.SignedTransactions, Is.EqualTo(new[] { "c2lnbmVk" }));
            Assert.That(result.Error, Is.Empty);
        }

        [Test]
        public async Task SignTransaction_NonBase64Transaction_ReturnsInvalidRequest()
        {
            SetClaims(new Claim("sign", "true"));
            SetBearerToken("tok");

            var result = await CreateTool().SignTransaction(new List<string> { "not-base64!!" });

            Assert.That(result.ErrorType, Is.EqualTo("InvalidRequest"));
        }

        // ───────────────────────── executeAlgorandTransaction ─────────────────────────

        [Test]
        public async Task ExecuteTransaction_MissingSignClaim_ReturnsInsufficientScope()
        {
            SetClaims();
            SetBearerToken("tok");

            var result = await CreateTool().ExecuteTransaction(new List<string> { "AA==" });

            Assert.That(result.ErrorType, Is.EqualTo("InsufficientScope"));
        }

        [Test]
        public async Task ExecuteTransaction_EmptyList_ReturnsInvalidRequest()
        {
            SetClaims(new Claim("sign", "true"));
            SetBearerToken("tok");

            var result = await CreateTool().ExecuteTransaction(new List<string>());

            Assert.That(result.ErrorType, Is.EqualTo("InvalidRequest"));
        }

        // ───────────────────────── createSwapTransaction ─────────────────────────

        [Test]
        public async Task CreateSwapTransaction_BiatecRouterQuotesBest_ReturnsErrorWhenAlsoUnauthenticated()
        {
            // No bearer token/claim at all - even though Biatec Router's quote wins, building the actual
            // transaction still needs to resolve a sender address, which requires authentication.
            SetClaims();
            _biatecRouterQuoteProvider
                .Setup(p => p.GetQuoteAsync(0, 31566704, 1_000_000, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new DexQuote { ProviderName = "BiatecRouter", OutputAmount = 500 });

            var result = await CreateTool().CreateSwapTransaction(0, 31566704, 1_000_000);

            Assert.That(result.BestProvider, Is.EqualTo("BiatecRouter"));
            Assert.That(result.Error, Does.Contain("bearer token"));
        }

        [Test]
        public async Task CreateSwapTransaction_CompetitorQuotesBest_ReturnsComparisonWithoutBuildingTransaction()
        {
            SetClaims();
            var competitor = new Mock<IDexQuoteProvider>();
            competitor.Setup(p => p.ProviderName).Returns("FolksRouter");
            competitor
                .Setup(p => p.GetQuoteAsync(0, 31566704, 1_000_000, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new DexQuote { ProviderName = "FolksRouter", OutputAmount = 999 });
            _biatecRouterQuoteProvider
                .Setup(p => p.GetQuoteAsync(0, 31566704, 1_000_000, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new DexQuote { ProviderName = "BiatecRouter", OutputAmount = 500 });
            var aggregator = new DexSwapAggregatorService(new[] { _biatecRouterQuoteProvider.Object, competitor.Object });

            var result = await CreateTool(aggregator).CreateSwapTransaction(0, 31566704, 1_000_000);

            Assert.That(result.BestProvider, Is.EqualTo("FolksRouter"));
            Assert.That(result.UnsignedTransactions, Is.Empty);
            Assert.That(result.ErrorType, Is.EqualTo("TransactionBuildingNotAvailable"));
            Assert.That(result.Quotes, Has.Count.EqualTo(2));
        }

        [Test]
        public async Task CreateSwapTransaction_NoQuotesAvailable_ReturnsClearError()
        {
            SetClaims();
            _biatecRouterQuoteProvider
                .Setup(p => p.GetQuoteAsync(It.IsAny<long>(), It.IsAny<long>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((DexQuote?)null);

            var result = await CreateTool().CreateSwapTransaction(0, 31566704, 1_000_000);

            Assert.That(result.ErrorType, Is.EqualTo("NoQuoteAvailable"));
        }

        // ───────────────────────── createBridgeTransaction (Aramid Finance) ─────────────────────────

        private static AramidConfigRoot ValidAramidConfig() => new()
        {
            Chains = new Dictionary<string, AramidChainItem>
            {
                ["416001"] = new AramidChainItem { ChainId = 416001, Type = "algo", Address = "BRIDGEDEPOSITADDRESSXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX", Tokens = { ["0"] = new AramidTokenItem { Decimals = 6 } } },
                ["416101"] = new AramidChainItem { ChainId = 416101, Type = "algo", Address = "VOIBRIDGEXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX", Tokens = { ["302189"] = new AramidTokenItem { Decimals = 6 } } }
            },
            Chains2Tokens = new Dictionary<string, Dictionary<string, Dictionary<string, Dictionary<string, AramidMappingItem>>>>
            {
                ["416001"] = new()
                {
                    ["416101"] = new()
                    {
                        ["0"] = new()
                        {
                            ["302189"] = new AramidMappingItem
                            {
                                FeeAlternatives = { new AramidFeeAlternative { MinimumAmount = 1000, MaximumAmount = 1_000_000_000, SourcePercent = 0.001m, SourceConst = 0 } }
                            }
                        }
                    }
                }
            }
        };

        [Test]
        public async Task CreateBridgeTransaction_NoBearerTokenAndNoClaim_ReturnsUnauthorized()
        {
            SetClaims();

            var result = await CreateTool().CreateBridgeTransaction(0, 1_000_000, 416101, "VOIRECIPIENT", "302189");

            Assert.That(result.Error, Does.Contain("bearer token"));
            _aramidBridgeConfigProvider.Verify(p => p.GetConfigAsync(It.IsAny<CancellationToken>()), Times.Never);
        }

        [Test]
        public async Task CreateBridgeTransaction_UnsupportedGenesisId_ReturnsInvalidRequest()
        {
            SetClaims(new Claim("algorand_address", "SOMEADDRESS"));

            var result = await CreateTool().CreateBridgeTransaction(0, 1_000_000, 416101, "VOIRECIPIENT", "302189", genesisId: "testnet-v1.0");

            Assert.That(result.ErrorType, Is.EqualTo("InvalidRequest"));
            _aramidBridgeConfigProvider.Verify(p => p.GetConfigAsync(It.IsAny<CancellationToken>()), Times.Never);
        }

        [Test]
        public async Task CreateBridgeTransaction_UnknownDestinationChain_ReturnsError()
        {
            SetClaims(new Claim("algorand_address", "SOMEADDRESS"));
            _aramidBridgeConfigProvider.Setup(p => p.GetConfigAsync(It.IsAny<CancellationToken>())).ReturnsAsync(ValidAramidConfig());

            var result = await CreateTool().CreateBridgeTransaction(0, 1_000_000, 999999, "SOMEADDR", "0");

            Assert.That(result.ErrorType, Is.EqualTo("UnknownDestinationChain"));
        }

        [Test]
        public async Task CreateBridgeTransaction_RouteNotFound_ReturnsError()
        {
            SetClaims(new Claim("algorand_address", "SOMEADDRESS"));
            _aramidBridgeConfigProvider.Setup(p => p.GetConfigAsync(It.IsAny<CancellationToken>())).ReturnsAsync(ValidAramidConfig());

            // Asset 12345 has no configured route to Voi in ValidAramidConfig().
            var result = await CreateTool().CreateBridgeTransaction(12345, 1_000_000, 416101, "VOIRECIPIENT", "302189");

            Assert.That(result.ErrorType, Is.EqualTo("RouteNotFound"));
        }

        [Test]
        public async Task CreateBridgeTransaction_ValidRoute_ReachesAlgodStage()
        {
            // No Algod network is configured for "mainnet-v1.0" in this test's AlgodConfiguration, so the
            // call fails deterministically once it reaches that stage (ArgumentException) - proving config
            // fetch + route/chain resolution all succeeded first.
            SetClaims(new Claim("algorand_address", "SOMEADDRESS"));
            _aramidBridgeConfigProvider.Setup(p => p.GetConfigAsync(It.IsAny<CancellationToken>())).ReturnsAsync(ValidAramidConfig());

            var result = await CreateTool().CreateBridgeTransaction(0, 1_000_000, 416101, "VOIRECIPIENT", "302189");

            Assert.That(result.ErrorType, Does.Contain("ArgumentException"));
        }

        // ───────────────────────── getBridgeConfiguration ─────────────────────────

        [Test]
        public async Task GetBridgeConfiguration_ReturnsChainsAndRoutesFromAlgorandMainnet()
        {
            _aramidBridgeConfigProvider.Setup(p => p.GetConfigAsync(It.IsAny<CancellationToken>())).ReturnsAsync(ValidAramidConfig());

            var result = await CreateTool().GetBridgeConfiguration();

            Assert.That(result.Error, Is.Empty);
            Assert.That(result.Chains.Select(c => c.ChainId), Is.EquivalentTo(new long[] { 416001, 416101 }));
            Assert.That(result.RoutesFromAlgorandMainnet, Has.Count.EqualTo(1));
            var route = result.RoutesFromAlgorandMainnet[0];
            Assert.That(route.DestinationChainId, Is.EqualTo(416101));
            Assert.That(route.SourceToken, Is.EqualTo("0"));
            Assert.That(route.DestinationToken, Is.EqualTo("302189"));
            Assert.That(route.FeeAlternatives, Has.Count.EqualTo(1));
            Assert.That(route.FeeAlternatives[0].MinimumAmount, Is.EqualTo(1000));
        }

        [Test]
        public async Task GetBridgeConfiguration_FilteredByDestinationChainId_ExcludesOtherRoutes()
        {
            _aramidBridgeConfigProvider.Setup(p => p.GetConfigAsync(It.IsAny<CancellationToken>())).ReturnsAsync(ValidAramidConfig());

            var result = await CreateTool().GetBridgeConfiguration(destinationChainId: 999999);

            Assert.That(result.RoutesFromAlgorandMainnet, Is.Empty);
        }

        [Test]
        public async Task GetBridgeConfiguration_NoRoutesFromMainnet_ReturnsEmptyRoutesNotError()
        {
            _aramidBridgeConfigProvider.Setup(p => p.GetConfigAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new AramidConfigRoot
            {
                Chains = new Dictionary<string, AramidChainItem>
                {
                    ["416001"] = new AramidChainItem { ChainId = 416001, Type = "algo", Address = "BRIDGEDEPOSITADDRESSXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX" }
                }
            });

            var result = await CreateTool().GetBridgeConfiguration();

            Assert.That(result.Error, Is.Empty);
            Assert.That(result.RoutesFromAlgorandMainnet, Is.Empty);
        }

        [Test]
        public async Task GetBridgeConfiguration_ConfigProviderThrows_ReturnsError()
        {
            _aramidBridgeConfigProvider.Setup(p => p.GetConfigAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("IPFS unreachable"));

            var result = await CreateTool().GetBridgeConfiguration();

            Assert.That(result.Error, Is.Not.Empty);
        }

        // ───────────────────────── createMultisigTransaction / mergeMultisigTransactions ─────────────────────────

        [Test]
        public async Task CreateMultisigTransaction_NoParticipants_ReturnsInvalidRequest()
        {
            var result = await CreateTool().CreateMultisigTransaction(1, 1, new List<string>());

            Assert.That(result.ErrorType, Is.EqualTo("InvalidRequest"));
        }

        [Test]
        public async Task CreateMultisigTransaction_ThresholdExceedsParticipantCount_ReturnsInvalidRequest()
        {
            var result = await CreateTool().CreateMultisigTransaction(1, 5, new List<string> { "I3IINASAS7SKHFOP75DGTHDTYSQ42EBUCNNU5I3PQSSUVX32B2QIOTXIWU" });

            Assert.That(result.ErrorType, Is.EqualTo("InvalidRequest"));
        }

        [Test]
        public async Task CreateMultisigTransaction_InvalidParticipantAddress_ReturnsError()
        {
            var result = await CreateTool().CreateMultisigTransaction(1, 1, new List<string> { "not-a-real-address" });

            Assert.That(result.Error, Is.Not.Empty);
        }

        [Test]
        public void MergeMultisigTransactions_EmptyList_ReturnsInvalidRequest()
        {
            var result = CreateTool().MergeMultisigTransactions(new List<string>()).Result;

            Assert.That(result.ErrorType, Is.EqualTo("InvalidRequest"));
        }
    }
}
