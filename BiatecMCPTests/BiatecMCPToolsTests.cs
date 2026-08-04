using System.Security.Claims;
using BiatecMCP.BusinessLogic;
using BiatecMCP.Model;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
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
            _chainRegistry.Setup(r => r.TryGetChainAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync((AlgorandChain?)null);
            _chainRegistry.Setup(r => r.TryGetChainByAramidIdAsync(It.IsAny<long>(), It.IsAny<CancellationToken>())).ReturnsAsync((AlgorandChain?)null);
            _networkResolver = new Mock<INetworkResolver>();
            _networkResolver.Setup(r => r.ListNetworksAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new List<NetworkSummary>());
            _evmRpcDataSource = new Mock<IPublicEvmRpcDataSource>();
            _bitcoinDataSource = new Mock<IPublicBitcoinDataSource>();
        }

        private BiatecMCP.MCP.BiatecMCP CreateTool(DexSwapAggregatorService? aggregator = null) =>
            new(_walletClient.Object, _httpContextAccessor,
                aggregator ?? new DexSwapAggregatorService(new[] { _biatecRouterQuoteProvider.Object }),
                _aramidBridgeConfigProvider.Object,
                _chainRegistry.Object,
                _networkResolver.Object,
                _evmRpcDataSource.Object,
                _bitcoinDataSource.Object,
                NullLogger<BiatecMCP.MCP.BiatecMCP>.Instance);

        private void SetBearerToken(string token) =>
            _httpContext.Request.Headers.Authorization = $"Bearer {token}";

        private void SetClaims(params Claim[] claims) =>
            _httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));

        // getAlgorandAddress was removed - getCryptoAddress(network: "Algorand") is the one canonical
        // address-lookup tool now (same underlying ResolveDerivedAddressAsync resolution), so these cover
        // that shared resolution logic through the surviving tool rather than duplicating it.

        private void SetAlgorandNetworkResolvable() =>
            _networkResolver
                .Setup(r => r.ResolveAsync("Algorand", It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ResolvedNetwork { Family = ChainFamily.Avm, DisplayName = "Algorand Mainnet", AvmChain = new AlgorandChain { GenesisId = "mainnet-v1.0" } });

        [Test]
        public async Task GetCryptoAddress_AvmPrimarySeedAddressClaimStale_FallsBackToLiveSeedList()
        {
            // Regression coverage: primary_seed_address is captured once at token issuance and carried
            // forward unchanged across refreshes, so a long-lived token can end up caching a seed
            // selector that no longer resolves (BiatecOIDC returns a "seed_not_found" ProblemDetails).
            // The tool must self-heal via GET /wallet/seeds instead of surfacing that error outright.
            SetAlgorandNetworkResolvable();
            SetClaims(new Claim("primary_seed_address", "STALESEED"));
            SetBearerToken("tok");
            _walletClient
                .Setup(c => c.GetAddressAsync("tok", "STALESEED", 0, It.IsAny<CancellationToken>()))
                .ThrowsAsync(new WalletApiException(400, "seed_not_found", "No seed with address 'STALESEED' exists for this account."));
            _walletClient
                .Setup(c => c.ListSeedsAsync("tok", It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ListSeedsResponse { Seeds = { new SeedResponse { Address = "REALPRIMARY", IsPrimary = true } } });
            _walletClient
                .Setup(c => c.GetAddressAsync("tok", "REALPRIMARY", 0, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new DerivedAddressResponse { Address = "REALADDRESS", SeedAddress = "REALPRIMARY", Slot = 0 });

            var result = await CreateTool().GetCryptoAddress("Algorand");

            Assert.That(result.Address, Is.EqualTo("REALADDRESS"));
            Assert.That(result.Error, Is.Empty);
        }

        [Test]
        public async Task GetCryptoAddress_AvmExplicitSeedAddressNotFound_DoesNotFallBackAndSurfacesError()
        {
            // Unlike a claim-derived selector, a caller-supplied seedAddress was named on purpose - a
            // real "no such seed" for it must stay a clear error, never silently swapped for a
            // different seed's address.
            SetAlgorandNetworkResolvable();
            SetClaims();
            SetBearerToken("tok");
            _walletClient
                .Setup(c => c.GetAddressAsync("tok", "NOTAREALSEED", 0, It.IsAny<CancellationToken>()))
                .ThrowsAsync(new WalletApiException(400, "seed_not_found", "No seed with address 'NOTAREALSEED' exists for this account."));

            var result = await CreateTool().GetCryptoAddress("Algorand", seedAddress: "NOTAREALSEED");

            Assert.That(result.Address, Is.Empty);
            Assert.That(result.Error, Does.Contain("NOTAREALSEED"));
            _walletClient.Verify(c => c.ListSeedsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Test]
        public async Task GetCryptoAddress_AvmNoClaimAndNoSeeds_ReturnsClearError()
        {
            SetAlgorandNetworkResolvable();
            SetClaims();
            SetBearerToken("tok");
            _walletClient.Setup(c => c.ListSeedsAsync("tok", It.IsAny<CancellationToken>())).ReturnsAsync(new ListSeedsResponse());

            var result = await CreateTool().GetCryptoAddress("Algorand");

            Assert.That(result.Address, Is.Empty);
            Assert.That(result.Error, Does.Contain("no Algorand address"));
        }

        [Test]
        public async Task GetCryptoAddress_AvmNoClaimAndNoBearerToken_ReturnsUnauthorizedMessage()
        {
            SetAlgorandNetworkResolvable();
            SetClaims();
            // no bearer token set at all

            var result = await CreateTool().GetCryptoAddress("Algorand");

            Assert.That(result.Error, Does.Contain("bearer token"));
        }

        // ───────────────────────── Multi-address (ARC-76 slot) support ─────────────────────────

        [Test]
        public async Task GetCryptoAddress_AvmNonZeroSlot_UsesPrimarySeedAddressClaimAsSeedSelector()
        {
            SetAlgorandNetworkResolvable();
            SetClaims(new Claim("primary_seed_address", "PRIMARY"));
            SetBearerToken("tok");
            _walletClient
                .Setup(c => c.GetAddressAsync("tok", "PRIMARY", 1, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new DerivedAddressResponse { Address = "SECOND-ADDRESS", SeedAddress = "PRIMARY", Slot = 1 });

            var result = await CreateTool().GetCryptoAddress("Algorand", slot: 1);

            Assert.That(result.Address, Is.EqualTo("SECOND-ADDRESS"));
            _walletClient.Verify(c => c.ListSeedsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Test]
        public async Task GetCryptoAddress_AvmExplicitSeedAddress_DerivesFromThatSeedViaWalletApi()
        {
            SetAlgorandNetworkResolvable();
            SetClaims();
            SetBearerToken("tok");
            _walletClient
                .Setup(c => c.GetAddressAsync("tok", "OTHER-SEED", 0, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new DerivedAddressResponse { Address = "OTHER-DERIVED", SeedAddress = "OTHER-SEED", Slot = 0 });

            var result = await CreateTool().GetCryptoAddress("Algorand", seedAddress: "OTHER-SEED");

            Assert.That(result.Address, Is.EqualTo("OTHER-DERIVED"));
            _walletClient.Verify(c => c.ListSeedsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Test]
        public async Task ListAlgorandAddresses_ReturnsAddressesFromWalletClient()
        {
            SetBearerToken("tok");
            _walletClient
                .Setup(c => c.ListSeedsAsync("tok", It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ListSeedsResponse
                {
                    Seeds = { new SeedResponse { Address = "A1", IsPrimary = true }, new SeedResponse { Address = "A2", IsPrimary = false } }
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

        // ───────────────────────── listSupportedNetworks / getCryptoAddress / getCryptoBalance ─────────────────────────

        [Test]
        public async Task ListSupportedNetworks_ReturnsResolversNetworkList()
        {
            _networkResolver
                .Setup(r => r.ListNetworksAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<NetworkSummary>
                {
                    new() { Name = "Algorand Mainnet", Family = "Avm", Id = "mainnet-v1.0", NativeCurrencySymbol = "ALGO" },
                    new() { Name = "Ethereum", Family = "Evm", Id = "1", NativeCurrencySymbol = "ETH" }
                });

            var result = await CreateTool().ListSupportedNetworks();

            Assert.That(result.Networks, Has.Count.EqualTo(2));
            Assert.That(result.Error, Is.Empty);
        }

        [Test]
        public async Task GetCryptoAddress_UnknownNetwork_ReturnsClearError()
        {
            _networkResolver.Setup(r => r.ResolveAsync("NotAThing", It.IsAny<CancellationToken>())).ReturnsAsync((ResolvedNetwork?)null);

            var result = await CreateTool().GetCryptoAddress("NotAThing");

            Assert.That(result.Error, Does.Contain("Unknown network"));
        }

        [Test]
        public async Task GetCryptoAddress_AvmNetwork_DelegatesToAlgorandAddressResolution()
        {
            SetClaims(new Claim("primary_seed_address", "SOMESEED"));
            SetBearerToken("tok");
            _walletClient
                .Setup(c => c.GetAddressAsync("tok", "SOMESEED", 0, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new DerivedAddressResponse { Address = "SOMEADDRESS", SeedAddress = "SOMESEED", Slot = 0 });
            _networkResolver
                .Setup(r => r.ResolveAsync("Algorand", It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ResolvedNetwork { Family = ChainFamily.Avm, DisplayName = "Algorand Mainnet", AvmChain = new AlgorandChain { GenesisId = "mainnet-v1.0" } });

            var result = await CreateTool().GetCryptoAddress("Algorand");

            Assert.That(result.Address, Is.EqualTo("SOMEADDRESS"));
            Assert.That(result.Family, Is.EqualTo("Avm"));
            Assert.That(result.Network, Is.EqualTo("Algorand Mainnet"));
            _walletClient.Verify(c => c.ListSeedsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Test]
        public async Task GetCryptoAddress_EvmNetwork_CallsWalletClient()
        {
            SetBearerToken("tok");
            _networkResolver
                .Setup(r => r.ResolveAsync("Ethereum", It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ResolvedNetwork { Family = ChainFamily.Evm, DisplayName = "Ethereum Mainnet", EvmChain = new EvmChain { ChainId = 1, Name = "Ethereum Mainnet" } });
            _walletClient
                .Setup(c => c.ListSeedsAsync("tok", It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ListSeedsResponse { Seeds = { new SeedResponse { Address = "PRIMARY", IsPrimary = true } } });
            _walletClient
                .Setup(c => c.GetAddressAsync("tok", "PRIMARY", 0, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new DerivedAddressResponse { EvmAddress = "0xEVM" });

            var result = await CreateTool().GetCryptoAddress("Ethereum");

            Assert.That(result.Address, Is.EqualTo("0xEVM"));
            Assert.That(result.Family, Is.EqualTo("Evm"));
            Assert.That(result.Network, Is.EqualTo("Ethereum Mainnet"));
        }

        [Test]
        public async Task GetCryptoAddress_BitcoinNetwork_CallsWalletClient()
        {
            SetBearerToken("tok");
            _networkResolver
                .Setup(r => r.ResolveAsync("Bitcoin", It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ResolvedNetwork { Family = ChainFamily.Btc, DisplayName = "Bitcoin" });
            _walletClient
                .Setup(c => c.ListSeedsAsync("tok", It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ListSeedsResponse { Seeds = { new SeedResponse { Address = "PRIMARY", IsPrimary = true } } });
            _walletClient
                .Setup(c => c.GetAddressAsync("tok", "PRIMARY", 0, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new DerivedAddressResponse { BitcoinAddress = "bc1qsomeaddress" });

            var result = await CreateTool().GetCryptoAddress("Bitcoin");

            Assert.That(result.Address, Is.EqualTo("bc1qsomeaddress"));
            Assert.That(result.Family, Is.EqualTo("Btc"));
        }

        [Test]
        public async Task GetCryptoAddress_BitcoinCashNetwork_CallsWalletClient()
        {
            SetBearerToken("tok");
            _networkResolver
                .Setup(r => r.ResolveAsync("BitcoinCash", It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ResolvedNetwork { Family = ChainFamily.Bch, DisplayName = "Bitcoin Cash" });
            _walletClient
                .Setup(c => c.ListSeedsAsync("tok", It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ListSeedsResponse { Seeds = { new SeedResponse { Address = "PRIMARY", IsPrimary = true } } });
            _walletClient
                .Setup(c => c.GetAddressAsync("tok", "PRIMARY", 0, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new DerivedAddressResponse { BitcoinCashAddress = "bitcoincash:qsomeaddress" });

            var result = await CreateTool().GetCryptoAddress("BitcoinCash");

            Assert.That(result.Address, Is.EqualTo("bitcoincash:qsomeaddress"));
            Assert.That(result.Family, Is.EqualTo("Bch"));
        }

        [Test]
        public async Task GetCryptoBalance_BitcoinNetwork_ReturnsBalanceFromDataSource()
        {
            SetBearerToken("tok");
            _networkResolver
                .Setup(r => r.ResolveAsync("Bitcoin", It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ResolvedNetwork { Family = ChainFamily.Btc, DisplayName = "Bitcoin" });
            _bitcoinDataSource
                .Setup(d => d.TryGetBalanceAsync(BlockchairChainSlugs.Bitcoin, "bc1qaddr", It.IsAny<CancellationToken>()))
                .ReturnsAsync(150_000_000L);

            var result = await CreateTool().GetCryptoBalance("Bitcoin", address: "bc1qaddr");

            Assert.That(result.Error, Is.Empty);
            Assert.That(result.NativeCurrencySymbol, Is.EqualTo("BTC"));
            Assert.That(result.NativeBalance, Is.EqualTo(1.5m));
            Assert.That(result.NativeBalanceBaseUnits, Is.EqualTo("150000000"));
        }

        [Test]
        public async Task GetCryptoBalance_UnknownNetwork_ReturnsClearError()
        {
            _networkResolver.Setup(r => r.ResolveAsync("NotAThing", It.IsAny<CancellationToken>())).ReturnsAsync((ResolvedNetwork?)null);

            var result = await CreateTool().GetCryptoBalance("NotAThing");

            Assert.That(result.Error, Does.Contain("Unknown network"));
        }

        [Test]
        public async Task GetCryptoBalance_EvmNetwork_ExplicitAddress_NoWalletApiCallNeeded()
        {
            _networkResolver
                .Setup(r => r.ResolveAsync("Ethereum", It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ResolvedNetwork
                {
                    Family = ChainFamily.Evm,
                    DisplayName = "Ethereum Mainnet",
                    EvmChain = new EvmChain { ChainId = 1, Name = "Ethereum Mainnet", RpcUrl = "https://eth.example.com", NativeCurrencySymbol = "ETH", NativeCurrencyDecimals = 18 }
                });
            _evmRpcDataSource
                .Setup(d => d.TryGetBalanceAsync("https://eth.example.com", "0xSOMEADDR", It.IsAny<CancellationToken>()))
                .ReturnsAsync(System.Numerics.BigInteger.Parse("1500000000000000000"));

            var result = await CreateTool().GetCryptoBalance("Ethereum", address: "0xSOMEADDR");

            Assert.That(result.Error, Is.Empty);
            Assert.That(result.Address, Is.EqualTo("0xSOMEADDR"));
            Assert.That(result.NativeCurrencySymbol, Is.EqualTo("ETH"));
            Assert.That(result.NativeBalance, Is.EqualTo(1.5m));
            Assert.That(result.NativeBalanceBaseUnits, Is.EqualTo("1500000000000000000"));
            _walletClient.Verify(c => c.ListSeedsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Test]
        public async Task GetCryptoBalance_EvmNetwork_RpcUnreachable_ReturnsClearError()
        {
            _networkResolver
                .Setup(r => r.ResolveAsync("Ethereum", It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ResolvedNetwork
                {
                    Family = ChainFamily.Evm,
                    DisplayName = "Ethereum Mainnet",
                    EvmChain = new EvmChain { ChainId = 1, Name = "Ethereum Mainnet", RpcUrl = "https://eth.example.com", NativeCurrencySymbol = "ETH", NativeCurrencyDecimals = 18 }
                });
            _evmRpcDataSource
                .Setup(d => d.TryGetBalanceAsync("https://eth.example.com", "0xSOMEADDR", It.IsAny<CancellationToken>()))
                .ReturnsAsync((System.Numerics.BigInteger?)null);

            var result = await CreateTool().GetCryptoBalance("Ethereum", address: "0xSOMEADDR");

            Assert.That(result.ErrorType, Is.EqualTo("RpcUnavailable"));
        }

        [Test]
        public async Task GetCryptoBalance_AvmNetwork_NoAddressResolvable_ReturnsClearError()
        {
            SetClaims();
            SetBearerToken("tok");
            _networkResolver
                .Setup(r => r.ResolveAsync("Algorand", It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ResolvedNetwork { Family = ChainFamily.Avm, DisplayName = "Algorand Mainnet", AvmChain = new AlgorandChain { GenesisId = "mainnet-v1.0" } });
            _walletClient.Setup(c => c.ListSeedsAsync("tok", It.IsAny<CancellationToken>())).ReturnsAsync(new ListSeedsResponse());

            var result = await CreateTool().GetCryptoBalance("Algorand");

            Assert.That(result.Error, Does.Contain("no Algorand address"));
        }

        // ───────────────────────── getAddressInfo / activateCryptoAddress ─────────────────────────

        [Test]
        public async Task GetAddressInfo_ForwardsToWalletClientAndReturnsResult()
        {
            SetBearerToken("tok");
            _walletClient
                .Setup(c => c.GetAddressInfoAsync("tok", "algorand", "ADDR1", It.IsAny<CancellationToken>()))
                .ReturnsAsync(new AddressInfoResponse { Address = "ADDR1", Network = "algorand", Family = "Avm", IsActive = true, SeedAddress = "SEED1", Slot = 2 });

            var result = await CreateTool().GetAddressInfo("algorand", "ADDR1");

            Assert.That(result.IsActive, Is.True);
            Assert.That(result.SeedAddress, Is.EqualTo("SEED1"));
            Assert.That(result.Slot, Is.EqualTo(2));
            Assert.That(result.Error, Is.Empty);
        }

        [Test]
        public async Task GetAddressInfo_NoBearerToken_ReturnsUnauthorized()
        {
            var result = await CreateTool().GetAddressInfo("algorand", "ADDR1");

            Assert.That(result.ErrorType, Is.EqualTo("Unauthorized"));
        }

        [Test]
        public async Task GetAddressInfo_WalletApiThrows_ReturnsErrorFromException()
        {
            SetBearerToken("tok");
            _walletClient
                .Setup(c => c.GetAddressInfoAsync("tok", "algorand", "ADDR1", It.IsAny<CancellationToken>()))
                .ThrowsAsync(new WalletApiException(400, "unknown_network", "Unknown network 'algorand'."));

            var result = await CreateTool().GetAddressInfo("algorand", "ADDR1");

            Assert.That(result.ErrorType, Is.EqualTo("unknown_network"));
        }

        [Test]
        public async Task ActivateCryptoAddress_MissingSignClaim_ReturnsInsufficientScope()
        {
            SetClaims();
            SetBearerToken("tok");

            var result = await CreateTool().ActivateCryptoAddress("algorand", "SEED1", 0, "ADDR1");

            Assert.That(result.ErrorType, Is.EqualTo("InsufficientScope"));
            _walletClient.Verify(c => c.ActivateAddressAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Test]
        public async Task ActivateCryptoAddress_ValidRequest_ForwardsToWalletClient()
        {
            SetClaims(new Claim("sign", "true"));
            SetBearerToken("tok");
            _walletClient
                .Setup(c => c.ActivateAddressAsync("tok", "algorand", "SEED1", 3, "ADDR1", It.IsAny<CancellationToken>()))
                .ReturnsAsync(new AddressInfoResponse { Address = "ADDR1", Network = "algorand", Family = "Avm", IsActive = true });

            var result = await CreateTool().ActivateCryptoAddress("algorand", "SEED1", 3, "ADDR1");

            Assert.That(result.IsActive, Is.True);
            Assert.That(result.Error, Is.Empty);
        }

        [Test]
        public async Task ActivateCryptoAddress_RekeyNotConfirmed_ReturnsErrorFromException()
        {
            SetClaims(new Claim("sign", "true"));
            SetBearerToken("tok");
            _walletClient
                .Setup(c => c.ActivateAddressAsync("tok", "algorand", "SEED1", 0, "ADDR1", It.IsAny<CancellationToken>()))
                .ThrowsAsync(new WalletApiException(409, "rekey_not_confirmed", "Not yet rekeyed on-chain."));

            var result = await CreateTool().ActivateCryptoAddress("algorand", "SEED1", 0, "ADDR1");

            Assert.That(result.ErrorType, Is.EqualTo("rekey_not_confirmed"));
        }

        // ───────────────────────── createPaymentTransaction / createOptInTransaction (no sign claim needed - build only) ─────────────────────────

        [Test]
        public async Task CreatePaymentTransaction_NoBearerTokenAndNoClaim_ReturnsUnauthorized()
        {
            SetClaims(); // no primary_seed_address claim, no bearer token at all

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
                .ReturnsAsync(new DerivedAddressResponse { Address = "OTHER-SEED", SeedAddress = "OTHER-SEED", Slot = 10 });

            // The default 'network' ("algorand-mainnet") isn't set up on the mocked INetworkResolver, so the
            // call fails deterministically once it reaches that stage - proving the sender was resolved
            // through the wallet API first (an unresolvable network is an InvalidRequest, not a crash).
            var result = await CreateTool().CreatePaymentTransaction(receiverAccount: "SOME", amount: 1, seedAddress: "OTHER-SEED", slot: 10);

            _walletClient.Verify(c => c.GetAddressAsync("tok", "OTHER-SEED", 10, It.IsAny<CancellationToken>()), Times.Once);
            Assert.That(result.ErrorType, Is.EqualTo("InvalidRequest"));
            // Never touches the wallet's sign endpoint - this tool only builds, never signs.
            _walletClient.Verify(c => c.SignAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<byte[]>>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        // ───────────────────────── createBitcoinTransaction / executeTransaction (Bitcoin) ─────────────────────────

        [Test]
        public async Task CreateBitcoinTransaction_HappyPath_BuildsUnsignedTransactionFromUtxos()
        {
            SetClaims(new Claim("primary_seed_address", "SEED"));
            SetBearerToken("tok");
            _networkResolver
                .Setup(r => r.ResolveAsync("Bitcoin", It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ResolvedNetwork { Family = ChainFamily.Btc, DisplayName = "Bitcoin" });
            _walletClient
                .Setup(c => c.GetAddressAsync("tok", "SEED", 0, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new DerivedAddressResponse { BitcoinAddress = "bc1qsender" });
            _bitcoinDataSource
                .Setup(d => d.GetUtxosAsync(BlockchairChainSlugs.Bitcoin, "bc1qsender", It.IsAny<CancellationToken>()))
                .ReturnsAsync(new[] { new BitcoinUtxo("tx1", 0, 1_000_000) });
            _bitcoinDataSource
                .Setup(d => d.TryGetSuggestedFeeRateAsync(BlockchairChainSlugs.Bitcoin, It.IsAny<CancellationToken>()))
                .ReturnsAsync(1m);

            var result = await CreateTool().CreateBitcoinTransaction("bc1qreceiver", 500_000, "Bitcoin");

            Assert.That(result.Error, Is.Empty);
            Assert.That(result.Sender, Is.EqualTo("bc1qsender"));
            Assert.That(result.Receiver, Is.EqualTo("bc1qreceiver"));
            Assert.That(result.AmountSatoshis, Is.EqualTo(500_000));
            Assert.That(result.InputCount, Is.EqualTo(1));
            Assert.That(result.UnsignedTransaction, Is.Not.Empty);
        }

        [Test]
        public async Task CreateBitcoinTransaction_NoUtxos_ReturnsClearError()
        {
            SetClaims(new Claim("primary_seed_address", "SEED"));
            SetBearerToken("tok");
            _networkResolver
                .Setup(r => r.ResolveAsync("Bitcoin", It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ResolvedNetwork { Family = ChainFamily.Btc, DisplayName = "Bitcoin" });
            _walletClient
                .Setup(c => c.GetAddressAsync("tok", "SEED", 0, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new DerivedAddressResponse { BitcoinAddress = "bc1qsender" });
            _bitcoinDataSource
                .Setup(d => d.GetUtxosAsync(BlockchairChainSlugs.Bitcoin, "bc1qsender", It.IsAny<CancellationToken>()))
                .ReturnsAsync(Array.Empty<BitcoinUtxo>());

            var result = await CreateTool().CreateBitcoinTransaction("bc1qreceiver", 500_000, "Bitcoin");

            Assert.That(result.ErrorType, Is.EqualTo("NoUtxos"));
        }

        [Test]
        public async Task CreateBitcoinTransaction_UnknownNetwork_ReturnsInvalidRequest()
        {
            _networkResolver.Setup(r => r.ResolveAsync("NotAThing", It.IsAny<CancellationToken>())).ReturnsAsync((ResolvedNetwork?)null);

            var result = await CreateTool().CreateBitcoinTransaction("bc1qreceiver", 500_000, "NotAThing");

            Assert.That(result.ErrorType, Is.EqualTo("InvalidRequest"));
        }

        [Test]
        public async Task ExecuteTransaction_Bitcoin_MissingSignClaim_ReturnsInsufficientScope()
        {
            SetClaims();
            SetBearerToken("tok");

            var result = await CreateTool().ExecuteTransaction(new List<string> { Convert.ToBase64String(new byte[] { 1, 2, 3 }) }, "Bitcoin");

            Assert.That(result.ErrorType, Is.EqualTo("InsufficientScope"));
            _bitcoinDataSource.Verify(d => d.TryBroadcastAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Test]
        public async Task ExecuteTransaction_Bitcoin_ValidRequest_BroadcastsAndReturnsTxIdAndExplorerLink()
        {
            SetClaims(new Claim("sign", "true"));
            SetBearerToken("tok");
            _networkResolver
                .Setup(r => r.ResolveAsync("Bitcoin", It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ResolvedNetwork { Family = ChainFamily.Btc, DisplayName = "Bitcoin" });
            _bitcoinDataSource
                .Setup(d => d.TryBroadcastAsync(BlockchairChainSlugs.Bitcoin, "010203", It.IsAny<CancellationToken>()))
                .ReturnsAsync("abc123txid");

            var result = await CreateTool().ExecuteTransaction(new List<string> { Convert.ToBase64String(new byte[] { 1, 2, 3 }) }, "Bitcoin");

            Assert.That(result.Error, Is.Empty);
            Assert.That(result.TxId, Is.EqualTo("abc123txid"));
            Assert.That(result.ExplorerLink, Is.EqualTo("https://blockchair.com/bitcoin/transaction/abc123txid"));
        }

        [Test]
        public async Task ExecuteTransaction_Bitcoin_BroadcastFails_ReturnsClearError()
        {
            SetClaims(new Claim("sign", "true"));
            SetBearerToken("tok");
            _networkResolver
                .Setup(r => r.ResolveAsync("Bitcoin", It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ResolvedNetwork { Family = ChainFamily.Btc, DisplayName = "Bitcoin" });
            _bitcoinDataSource
                .Setup(d => d.TryBroadcastAsync(BlockchairChainSlugs.Bitcoin, It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string?)null);

            var result = await CreateTool().ExecuteTransaction(new List<string> { Convert.ToBase64String(new byte[] { 1, 2, 3 }) }, "Bitcoin");

            Assert.That(result.ErrorType, Is.EqualTo("BroadcastFailed"));
        }

        [Test]
        public async Task ExecuteTransaction_Bitcoin_MoreThanOneTransaction_ReturnsInvalidRequest()
        {
            SetClaims(new Claim("sign", "true"));
            SetBearerToken("tok");
            _networkResolver
                .Setup(r => r.ResolveAsync("Bitcoin", It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ResolvedNetwork { Family = ChainFamily.Btc, DisplayName = "Bitcoin" });

            var result = await CreateTool().ExecuteTransaction(new List<string> { "AQ==", "Ag==" }, "Bitcoin");

            Assert.That(result.ErrorType, Is.EqualTo("InvalidRequest"));
            _bitcoinDataSource.Verify(d => d.TryBroadcastAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Test]
        public async Task ExecuteTransaction_UnknownNetwork_ReturnsInvalidRequest()
        {
            SetClaims(new Claim("sign", "true"));
            SetBearerToken("tok");
            _networkResolver
                .Setup(r => r.ResolveAsync("notanetwork", It.IsAny<CancellationToken>()))
                .ReturnsAsync((ResolvedNetwork?)null);

            var result = await CreateTool().ExecuteTransaction(new List<string> { "AA==" }, "notanetwork");

            Assert.That(result.ErrorType, Is.EqualTo("InvalidRequest"));
        }

        [Test]
        public async Task ExecuteTransaction_EvmNetwork_ReturnsNotSupportedError()
        {
            SetClaims(new Claim("sign", "true"));
            SetBearerToken("tok");
            _networkResolver
                .Setup(r => r.ResolveAsync("Ethereum", It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ResolvedNetwork { Family = ChainFamily.Evm, DisplayName = "Ethereum Mainnet", EvmChain = new EvmChain { ChainId = 1, Name = "Ethereum Mainnet" } });

            var result = await CreateTool().ExecuteTransaction(new List<string> { "AA==" }, "Ethereum");

            Assert.That(result.ErrorType, Is.EqualTo("InvalidRequest"));
            Assert.That(result.Error, Does.Contain("EVM"));
        }

        [Test]
        public async Task CreateOptInTransaction_ZeroAssetId_ReturnsInvalidRequest()
        {
            SetClaims();

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

            var result = await CreateTool().SignTransaction(new List<string> { "AA==" }, "algorand", "ADDR");

            Assert.That(result.ErrorType, Is.EqualTo("InsufficientScope"));
            _walletClient.Verify(c => c.SignAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<byte[]>>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Test]
        public async Task SignTransaction_EmptyList_ReturnsInvalidRequest()
        {
            SetClaims(new Claim("sign", "true"));
            SetBearerToken("tok");

            var result = await CreateTool().SignTransaction(new List<string>(), "algorand", "ADDR");

            Assert.That(result.ErrorType, Is.EqualTo("InvalidRequest"));
        }

        [Test]
        public async Task SignTransaction_SignClaimPresentButNoBearerToken_ReturnsUnauthorized()
        {
            SetClaims(new Claim("sign", "true"));

            var result = await CreateTool().SignTransaction(new List<string> { "AA==" }, "algorand", "ADDR");

            Assert.That(result.ErrorType, Is.EqualTo("Unauthorized"));
        }

        [Test]
        public async Task SignTransaction_ValidRequest_ForwardsToWalletClientWithNetworkAndAddress()
        {
            SetClaims(new Claim("sign", "true"));
            SetBearerToken("tok");
            _networkResolver
                .Setup(r => r.ResolveAsync("algorand", It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ResolvedNetwork { Family = ChainFamily.Avm, DisplayName = "Algorand", AvmChain = new AlgorandChain { GenesisId = "mainnet-v1.0" } });
            _walletClient
                .Setup(c => c.SignAsync("tok", "algorand", "SEED-ADDR", It.IsAny<IReadOnlyList<byte[]>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new SignTransactionGroupResponse { SignedTransactions = { "c2lnbmVk" } });

            var result = await CreateTool().SignTransaction(new List<string> { Convert.ToBase64String(new byte[] { 1, 2, 3 }) }, "algorand", "SEED-ADDR");

            Assert.That(result.SignedTransactions, Is.EqualTo(new[] { "c2lnbmVk" }));
            Assert.That(result.Error, Is.Empty);
        }

        [Test]
        public async Task SignTransaction_NonBase64Transaction_ReturnsInvalidRequest()
        {
            SetClaims(new Claim("sign", "true"));
            SetBearerToken("tok");

            var result = await CreateTool().SignTransaction(new List<string> { "not-base64!!" }, "algorand", "ADDR");

            Assert.That(result.ErrorType, Is.EqualTo("InvalidRequest"));
        }

        [Test]
        public async Task SignTransaction_UnknownNetwork_ReturnsInvalidRequestListingValidCodesWithoutCallingWalletClient()
        {
            SetClaims(new Claim("sign", "true"));
            SetBearerToken("tok");
            _networkResolver
                .Setup(r => r.ResolveAsync("notanetwork", It.IsAny<CancellationToken>()))
                .ReturnsAsync((ResolvedNetwork?)null);
            _networkResolver
                .Setup(r => r.ListNetworksAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<NetworkSummary> { new() { Code = "algorand-mainnet" } });

            var result = await CreateTool().SignTransaction(new List<string> { "AA==" }, "notanetwork", "ADDR");

            Assert.That(result.ErrorType, Is.EqualTo("InvalidRequest"));
            Assert.That(result.Error, Does.Contain("algorand-mainnet"));
            _walletClient.Verify(c => c.SignAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<byte[]>>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        // ───────────────────────── executeTransaction (Algorand) ─────────────────────────

        [Test]
        public async Task ExecuteTransaction_Avm_MissingSignClaim_ReturnsInsufficientScope()
        {
            SetClaims();
            SetBearerToken("tok");

            var result = await CreateTool().ExecuteTransaction(new List<string> { "AA==" }, "mainnet-v1.0");

            Assert.That(result.ErrorType, Is.EqualTo("InsufficientScope"));
        }

        [Test]
        public async Task ExecuteTransaction_EmptyList_ReturnsInvalidRequest()
        {
            SetClaims(new Claim("sign", "true"));
            SetBearerToken("tok");

            var result = await CreateTool().ExecuteTransaction(new List<string>(), "mainnet-v1.0");

            Assert.That(result.ErrorType, Is.EqualTo("InvalidRequest"));
        }

        // ───────────────────────── GetAlgodSettings (explorer link resolution) ─────────────────────────
        //
        // Regression coverage: the dynamic-chain fallback used to hardcode "https://allo.info/tx/" for
        // every AVM network regardless of variant - Allo Explorer doesn't support Algorand testnet, so a
        // testnet execute/create call always returned a non-working explorer link. Fixed by resolving the
        // link from the chain's actual genesis id (KnownExplorerBaseUrls) instead of guessing one constant
        // for every network - a network with no known entry now gets no link at all rather than a wrong one.

        [Test]
        public async Task GetAlgodSettings_ConfiguredExplorerOverride_UsesConfiguredValue()
        {
            _networkResolver
                .Setup(r => r.ResolveAsync("algorand-mainnet", It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ResolvedNetwork
                {
                    Code = "algorand-mainnet",
                    Family = ChainFamily.Avm,
                    AvmChain = new AlgorandChain { GenesisId = "mainnet-v1.0", AlgodApiAddress = "https://algod.example.com" },
                    ConfiguredExplorerBaseUrlOverride = "https://custom.example.com/tx/"
                });

            var (_, _, explorerBaseUrl) = await CreateTool().GetAlgodSettings("algorand-mainnet");

            Assert.That(explorerBaseUrl, Is.EqualTo("https://custom.example.com/tx/"));
        }

        [Test]
        public async Task GetAlgodSettings_Mainnet_UsesBiatecScanExplorer()
        {
            _networkResolver
                .Setup(r => r.ResolveAsync("algorand-mainnet", It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ResolvedNetwork { Code = "algorand-mainnet", Family = ChainFamily.Avm, DisplayName = "Algorand", AvmChain = new AlgorandChain { GenesisId = "mainnet-v1.0", AlgodApiAddress = "https://algod.example.com" } });

            var (_, _, explorerBaseUrl) = await CreateTool().GetAlgodSettings("algorand-mainnet");

            Assert.That(explorerBaseUrl, Is.EqualTo("https://algorand.scan.biatec.io/transaction/"));
        }

        [Test]
        public async Task GetAlgodSettings_Testnet_UsesLoraTestnetExplorer()
        {
            _networkResolver
                .Setup(r => r.ResolveAsync("algorand-testnet", It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ResolvedNetwork { Code = "algorand-testnet", Family = ChainFamily.Avm, DisplayName = "Algorand Testnet", AvmChain = new AlgorandChain { GenesisId = "testnet-v1.0", AlgodApiAddress = "https://algod.example.com" } });

            var (_, _, explorerBaseUrl) = await CreateTool().GetAlgodSettings("algorand-testnet");

            Assert.That(explorerBaseUrl, Is.EqualTo("https://lora.algokit.io/testnet/transaction/"));
        }

        [Test]
        public async Task GetAlgodSettings_ChainWithNoKnownExplorer_ReturnsNoExplorerLinkRatherThanGuessing()
        {
            _networkResolver
                .Setup(r => r.ResolveAsync("voi-mainnet", It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ResolvedNetwork { Code = "voi-mainnet", Family = ChainFamily.Avm, DisplayName = "Voi Mainnet", AvmChain = new AlgorandChain { GenesisId = "voimain-v1.0", AlgodApiAddress = "https://algod.example.com" } });

            var (_, _, explorerBaseUrl) = await CreateTool().GetAlgodSettings("voi-mainnet");

            Assert.That(explorerBaseUrl, Is.Empty);
        }

        [Test]
        public void GetAlgodSettings_UnresolvableNetwork_ThrowsArgumentExceptionListingValidCodes()
        {
            _networkResolver
                .Setup(r => r.ResolveAsync("notanetwork", It.IsAny<CancellationToken>()))
                .ReturnsAsync((ResolvedNetwork?)null);
            _networkResolver
                .Setup(r => r.ListNetworksAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<NetworkSummary> { new() { Code = "algorand-mainnet" }, new() { Code = "bitcoin" } });

            var ex = Assert.ThrowsAsync<ArgumentException>(async () => await CreateTool().GetAlgodSettings("notanetwork"));

            Assert.That(ex!.Message, Does.Contain("algorand-mainnet"));
            Assert.That(ex.Message, Does.Contain("bitcoin"));
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
            SetClaims();

            var result = await CreateTool().CreateBridgeTransaction(0, 1_000_000, 416101, "VOIRECIPIENT", "302189", network: "testnet-v1.0");

            Assert.That(result.ErrorType, Is.EqualTo("InvalidRequest"));
            _aramidBridgeConfigProvider.Verify(p => p.GetConfigAsync(It.IsAny<CancellationToken>()), Times.Never);
        }

        [Test]
        public async Task CreateBridgeTransaction_UnknownDestinationChain_ReturnsError()
        {
            SetClaims(new Claim("primary_seed_address", "SOMEADDRESS"));
            SetBearerToken("tok");
            _walletClient
                .Setup(c => c.GetAddressAsync("tok", "SOMEADDRESS", 0, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new DerivedAddressResponse { Address = "SOMEADDRESS", SeedAddress = "SOMEADDRESS", Slot = 0 });
            _aramidBridgeConfigProvider.Setup(p => p.GetConfigAsync(It.IsAny<CancellationToken>())).ReturnsAsync(ValidAramidConfig());

            var result = await CreateTool().CreateBridgeTransaction(0, 1_000_000, 999999, "SOMEADDR", "0");

            Assert.That(result.ErrorType, Is.EqualTo("UnknownDestinationChain"));
        }

        [Test]
        public async Task CreateBridgeTransaction_RouteNotFound_ReturnsError()
        {
            SetClaims(new Claim("primary_seed_address", "SOMEADDRESS"));
            SetBearerToken("tok");
            _walletClient
                .Setup(c => c.GetAddressAsync("tok", "SOMEADDRESS", 0, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new DerivedAddressResponse { Address = "SOMEADDRESS", SeedAddress = "SOMEADDRESS", Slot = 0 });
            _aramidBridgeConfigProvider.Setup(p => p.GetConfigAsync(It.IsAny<CancellationToken>())).ReturnsAsync(ValidAramidConfig());

            // Asset 12345 has no configured route to Voi in ValidAramidConfig().
            var result = await CreateTool().CreateBridgeTransaction(12345, 1_000_000, 416101, "VOIRECIPIENT", "302189");

            Assert.That(result.ErrorType, Is.EqualTo("RouteNotFound"));
        }

        [Test]
        public async Task CreateBridgeTransaction_ValidRoute_ReachesAlgodStage()
        {
            // The default 'network' ("algorand-mainnet") isn't set up on the mocked INetworkResolver, so the
            // call fails deterministically once it reaches that stage - proving config fetch + route/chain
            // resolution all succeeded first.
            SetClaims(new Claim("primary_seed_address", "SOMEADDRESS"));
            SetBearerToken("tok");
            _walletClient
                .Setup(c => c.GetAddressAsync("tok", "SOMEADDRESS", 0, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new DerivedAddressResponse { Address = "SOMEADDRESS", SeedAddress = "SOMEADDRESS", Slot = 0 });
            _aramidBridgeConfigProvider.Setup(p => p.GetConfigAsync(It.IsAny<CancellationToken>())).ReturnsAsync(ValidAramidConfig());

            var result = await CreateTool().CreateBridgeTransaction(0, 1_000_000, 416101, "VOIRECIPIENT", "302189");

            Assert.That(result.ErrorType, Is.EqualTo("InvalidRequest"));
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
