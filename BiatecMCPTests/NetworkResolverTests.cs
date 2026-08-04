using BiatecMCP.BusinessLogic;
using BiatecMCP.Model;
using Microsoft.Extensions.Options;
using Moq;

namespace BiatecMCPTests
{
    /// <summary>
    /// Covers <see cref="NetworkResolver"/>'s strict canonical-code resolution - <see cref="NetworkResolver.ResolveAsync"/>
    /// only matches the exact (case-insensitive) codes <see cref="NetworkResolver.ListNetworksAsync"/> itself
    /// returns (see <see cref="INetworkResolver"/>'s remarks for why fuzzy/display-name/genesis-id/numeric-id
    /// matching was deliberately removed) - against mocked registries, no live HTTP.
    /// </summary>
    [TestFixture]
    public class NetworkResolverTests
    {
        private Mock<IAlgorandChainRegistry> _algorandChainRegistry = null!;
        private Mock<IEvmChainRegistry> _evmChainRegistry = null!;
        private AlgodConfiguration _algodConfigValue = null!;

        [SetUp]
        public void SetUp()
        {
            _algorandChainRegistry = new Mock<IAlgorandChainRegistry>();
            _algorandChainRegistry.Setup(r => r.GetSupportedChainsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new[]
            {
                new AlgorandChain { GenesisId = "mainnet-v1.0", Name = "Algorand", GenesisHash = "H1", AlgodApiAddress = "https://mainnet.example.com" },
                new AlgorandChain { GenesisId = "voimain-v1.0", Name = "Voi Mainnet", GenesisHash = "H2", AlgodApiAddress = "https://voi.example.com" },
                new AlgorandChain { GenesisId = "aramidmain-v1.0", Name = "Aramid Mainnet", GenesisHash = "H3", AlgodApiAddress = "https://aramid.example.com" }
            });

            _evmChainRegistry = new Mock<IEvmChainRegistry>();
            _algodConfigValue = new AlgodConfiguration();
        }

        private NetworkResolver CreateResolver() => new(
            _algorandChainRegistry.Object,
            _evmChainRegistry.Object,
            Mock.Of<IOptionsMonitor<AlgodConfiguration>>(m => m.CurrentValue == _algodConfigValue));

        // ───────────────────────── AVM: known codes ─────────────────────────

        [Test]
        public async Task ResolveAsync_AlgorandMainnetCode_ResolvesFromLiveRegistry()
        {
            var resolved = await CreateResolver().ResolveAsync("algorand-mainnet");

            Assert.That(resolved, Is.Not.Null);
            Assert.That(resolved!.Family, Is.EqualTo(ChainFamily.Avm));
            Assert.That(resolved.Code, Is.EqualTo("algorand-mainnet"));
            Assert.That(resolved.AvmChain!.GenesisId, Is.EqualTo("mainnet-v1.0"));
        }

        [Test]
        public async Task ResolveAsync_VoiMainnetCode_ResolvesFromLiveRegistry()
        {
            var resolved = await CreateResolver().ResolveAsync("voi-mainnet");

            Assert.That(resolved, Is.Not.Null);
            Assert.That(resolved!.AvmChain!.GenesisId, Is.EqualTo("voimain-v1.0"));
        }

        [Test]
        public async Task ResolveAsync_CodeIsCaseInsensitive()
        {
            var resolved = await CreateResolver().ResolveAsync("Algorand-Mainnet");

            Assert.That(resolved, Is.Not.Null);
            Assert.That(resolved!.AvmChain!.GenesisId, Is.EqualTo("mainnet-v1.0"));
        }

        [Test]
        public async Task ResolveAsync_ChainNotInKnownTable_SlugifiesFromLiveDisplayName()
        {
            // "Aramid Mainnet" isn't in the static KnownAvmCodesByGenesisId override table, so its code is
            // derived generically from the live registry's own reported name.
            var resolved = await CreateResolver().ResolveAsync("aramid-mainnet");

            Assert.That(resolved, Is.Not.Null);
            Assert.That(resolved!.AvmChain!.GenesisId, Is.EqualTo("aramidmain-v1.0"));
        }

        [Test]
        public async Task ResolveAsync_GenesisIdNoLongerAccepted_ReturnsNull()
        {
            // A raw genesis id is not a code - only "algorand-mainnet" resolves mainnet-v1.0 now.
            var resolved = await CreateResolver().ResolveAsync("mainnet-v1.0");

            Assert.That(resolved, Is.Null);
        }

        [Test]
        public async Task ResolveAsync_DisplayNameNoLongerAccepted_ReturnsNull()
        {
            var resolved = await CreateResolver().ResolveAsync("Voi Mainnet");

            Assert.That(resolved, Is.Null);
        }

        [Test]
        public async Task ResolveAsync_AlgorandTestnetCode_ResolvesFromLocalConfigOnly()
        {
            // Testnet never appears in the live genesis registry in this codebase - only a locally-configured
            // Algod:Networks entry, keyed by genesis id, ever produces it.
            _algodConfigValue.Networks["testnet-v1.0"] = new AlgodNetworkSettings { ApiAddress = "https://testnet.example.com" };

            var resolved = await CreateResolver().ResolveAsync("algorand-testnet");

            Assert.That(resolved, Is.Not.Null);
            Assert.That(resolved!.AvmChain!.GenesisId, Is.EqualTo("testnet-v1.0"));
            Assert.That(resolved.AvmChain.AlgodApiAddress, Is.EqualTo("https://testnet.example.com"));
        }

        [Test]
        public async Task ResolveAsync_LocalAlgodConfig_TakesPrecedenceOverLiveRegistry()
        {
            _algodConfigValue.Networks["mainnet-v1.0"] = new AlgodNetworkSettings { ApiAddress = "https://local-override.example.com", ApiToken = "local-token" };

            var resolved = await CreateResolver().ResolveAsync("algorand-mainnet");

            Assert.That(resolved, Is.Not.Null);
            Assert.That(resolved!.Family, Is.EqualTo(ChainFamily.Avm));
            Assert.That(resolved.AvmChain!.AlgodApiAddress, Is.EqualTo("https://local-override.example.com"));
        }

        // ───────────────────────── EVM: closed, well-known set only ─────────────────────────

        [Test]
        public async Task ResolveAsync_EthereumCode_Resolves()
        {
            var evmChain = new EvmChain { ChainId = 1, Name = "Ethereum", RpcUrl = "https://eth.example.com", NativeCurrencySymbol = "ETH" };
            _evmChainRegistry.Setup(r => r.TryGetChainAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(evmChain);

            var resolved = await CreateResolver().ResolveAsync("ethereum");

            Assert.That(resolved, Is.Not.Null);
            Assert.That(resolved!.Family, Is.EqualTo(ChainFamily.Evm));
            Assert.That(resolved.EvmChain!.ChainId, Is.EqualTo(1));
        }

        [Test]
        public async Task ResolveAsync_ArbitrumCode_ResolvesArbitrumOneChainId()
        {
            var evmChain = new EvmChain { ChainId = 42161, Name = "Arbitrum One", RpcUrl = "https://arb.example.com", NativeCurrencySymbol = "ETH" };
            _evmChainRegistry.Setup(r => r.TryGetChainAsync(42161, It.IsAny<CancellationToken>())).ReturnsAsync(evmChain);

            var resolved = await CreateResolver().ResolveAsync("arbitrum");

            Assert.That(resolved, Is.Not.Null);
            Assert.That(resolved!.EvmChain!.ChainId, Is.EqualTo(42161));
        }

        [Test]
        public async Task ResolveAsync_NumericEvmChainIdNoLongerAccepted_ReturnsNull()
        {
            var resolved = await CreateResolver().ResolveAsync("1");

            Assert.That(resolved, Is.Null);
        }

        [Test]
        public async Task ResolveAsync_ArbitraryEvmChainNameNoLongerAccepted_ReturnsNull()
        {
            // Previously any of ~2700 public EVM chains resolved by name via IEvmChainRegistry.TryGetChainByNameAsync -
            // now only the closed WellKnownEvmChains set (ethereum/gnosis/arbitrum/base) resolves at all.
            var resolved = await CreateResolver().ResolveAsync("Polygon");

            Assert.That(resolved, Is.Null);
            _evmChainRegistry.Verify(r => r.TryGetChainByNameAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Test]
        public async Task ResolveAsync_WellKnownEvmChainNotCurrentlyLive_ReturnsNull()
        {
            _evmChainRegistry.Setup(r => r.TryGetChainAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync((EvmChain?)null);

            var resolved = await CreateResolver().ResolveAsync("ethereum");

            Assert.That(resolved, Is.Null);
        }

        // ───────────────────────── Bitcoin-family ─────────────────────────

        [Test]
        public async Task ResolveAsync_BitcoinCode_Resolves()
        {
            var resolved = await CreateResolver().ResolveAsync("bitcoin");

            Assert.That(resolved, Is.Not.Null);
            Assert.That(resolved!.Family, Is.EqualTo(ChainFamily.Btc));
            Assert.That(resolved.Code, Is.EqualTo("bitcoin"));
        }

        [Test]
        public async Task ResolveAsync_BitcoinCashCode_Resolves()
        {
            var resolved = await CreateResolver().ResolveAsync("bitcoin-cash");

            Assert.That(resolved, Is.Not.Null);
            Assert.That(resolved!.Family, Is.EqualTo(ChainFamily.Bch));
        }

        [Test]
        public async Task ResolveAsync_OldStyleBitcoinCashSpelling_NoLongerAccepted()
        {
            var resolved = await CreateResolver().ResolveAsync("BitcoinCash");

            Assert.That(resolved, Is.Null);
        }

        // ───────────────────────── Misc ─────────────────────────

        [Test]
        public async Task ResolveAsync_UnresolvableNetwork_ReturnsNull()
        {
            var resolved = await CreateResolver().ResolveAsync("NotARealNetwork");

            Assert.That(resolved, Is.Null);
        }

        [Test]
        public async Task ResolveAsync_BlankNetwork_ReturnsNullWithoutCallingRegistries()
        {
            var resolved = await CreateResolver().ResolveAsync("   ");

            Assert.That(resolved, Is.Null);
            _algorandChainRegistry.Verify(r => r.GetSupportedChainsAsync(It.IsAny<CancellationToken>()), Times.Never);
        }

        [Test]
        public async Task ListNetworksAsync_ReturnsAvmChainsPlusLiveWellKnownEvmChainsPlusBitcoinFamily()
        {
            _evmChainRegistry.Setup(r => r.TryGetChainAsync(1, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new EvmChain { ChainId = 1, Name = "Ethereum", RpcUrl = "https://eth.example.com", NativeCurrencySymbol = "ETH" });
            _evmChainRegistry.Setup(r => r.TryGetChainAsync(100, It.IsAny<CancellationToken>())).ReturnsAsync((EvmChain?)null);
            _evmChainRegistry.Setup(r => r.TryGetChainAsync(42161, It.IsAny<CancellationToken>())).ReturnsAsync((EvmChain?)null);
            _evmChainRegistry.Setup(r => r.TryGetChainAsync(8453, It.IsAny<CancellationToken>())).ReturnsAsync((EvmChain?)null);

            var summaries = await CreateResolver().ListNetworksAsync();

            Assert.That(summaries.Count(s => s.Family == "Avm"), Is.EqualTo(3));
            Assert.That(summaries.Count(s => s.Family == "Evm"), Is.EqualTo(1));
            Assert.That(summaries.Single(s => s.Family == "Evm").Code, Is.EqualTo("ethereum"));
            Assert.That(summaries.Count(s => s.Family == "Btc"), Is.EqualTo(1));
            Assert.That(summaries.Count(s => s.Family == "Bch"), Is.EqualTo(1));
        }

        [Test]
        public async Task ListNetworksAsync_IncludesLocallyConfiguredTestnet()
        {
            _algodConfigValue.Networks["testnet-v1.0"] = new AlgodNetworkSettings { ApiAddress = "https://testnet.example.com" };

            var summaries = await CreateResolver().ListNetworksAsync();

            Assert.That(summaries.Any(s => s.Code == "algorand-testnet"), Is.True);
        }

        [Test]
        public async Task ListNetworksAsync_AllCodesAreUniqueAndResolveBackToThemselves()
        {
            // Every code ListNetworksAsync returns must round-trip through ResolveAsync - the exact
            // contract the "throw an error listing every valid code" UX depends on.
            _evmChainRegistry.Setup(r => r.TryGetChainAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((long chainId, CancellationToken _) => new EvmChain { ChainId = chainId, Name = "X", RpcUrl = "https://x.example.com", NativeCurrencySymbol = "X" });

            var resolver = CreateResolver();
            var summaries = await resolver.ListNetworksAsync();

            Assert.That(summaries.Select(s => s.Code).Distinct().Count(), Is.EqualTo(summaries.Count));
            foreach (var summary in summaries)
            {
                var resolved = await resolver.ResolveAsync(summary.Code);
                Assert.That(resolved, Is.Not.Null, $"Code '{summary.Code}' from ListNetworksAsync did not resolve back via ResolveAsync.");
                Assert.That(resolved!.Code, Is.EqualTo(summary.Code));
            }
        }
    }
}
