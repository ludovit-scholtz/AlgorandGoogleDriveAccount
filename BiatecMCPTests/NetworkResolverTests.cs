using BiatecMCP.BusinessLogic;
using BiatecMCP.Model;
using Microsoft.Extensions.Options;
using Moq;

namespace BiatecMCPTests
{
    /// <summary>
    /// Covers <see cref="NetworkResolver"/>'s "which chain did the user mean" unification across
    /// <see cref="IAlgorandChainRegistry"/> (AVM) and <see cref="IEvmChainRegistry"/> (EVM), against mocked
    /// registries - no live HTTP.
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
                new AlgorandChain { GenesisId = "mainnet-v1.0", Name = "Algorand Mainnet", GenesisHash = "H1", AlgodApiAddress = "https://mainnet.example.com" },
                new AlgorandChain { GenesisId = "voimain-v1.0", Name = "Voi Mainnet", GenesisHash = "H2", AlgodApiAddress = "https://voi.example.com" }
            });

            _evmChainRegistry = new Mock<IEvmChainRegistry>();
            _algodConfigValue = new AlgodConfiguration();
        }

        private NetworkResolver CreateResolver() => new(
            _algorandChainRegistry.Object,
            _evmChainRegistry.Object,
            Mock.Of<IOptionsMonitor<AlgodConfiguration>>(m => m.CurrentValue == _algodConfigValue));

        [Test]
        public async Task ResolveAsync_LocalAlgodConfig_TakesPrecedenceOverLiveRegistry()
        {
            _algodConfigValue.Networks["mainnet-v1.0"] = new AlgodNetworkSettings { ApiAddress = "https://local-override.example.com", ApiToken = "local-token" };

            var resolved = await CreateResolver().ResolveAsync("mainnet-v1.0");

            Assert.That(resolved, Is.Not.Null);
            Assert.That(resolved!.Family, Is.EqualTo(ChainFamily.Avm));
            Assert.That(resolved.AvmChain!.AlgodApiAddress, Is.EqualTo("https://local-override.example.com"));
        }

        [Test]
        public async Task ResolveAsync_AvmGenesisId_Resolves()
        {
            var resolved = await CreateResolver().ResolveAsync("voimain-v1.0");

            Assert.That(resolved, Is.Not.Null);
            Assert.That(resolved!.Family, Is.EqualTo(ChainFamily.Avm));
            Assert.That(resolved.AvmChain!.GenesisId, Is.EqualTo("voimain-v1.0"));
        }

        [Test]
        public async Task ResolveAsync_AvmNormalizedName_Resolves()
        {
            var resolved = await CreateResolver().ResolveAsync("Algorand");

            Assert.That(resolved, Is.Not.Null);
            Assert.That(resolved!.Family, Is.EqualTo(ChainFamily.Avm));
            Assert.That(resolved.AvmChain!.GenesisId, Is.EqualTo("mainnet-v1.0"));
        }

        [Test]
        public async Task ResolveAsync_AvmExactFullName_Resolves()
        {
            var resolved = await CreateResolver().ResolveAsync("Voi Mainnet");

            Assert.That(resolved, Is.Not.Null);
            Assert.That(resolved!.AvmChain!.GenesisId, Is.EqualTo("voimain-v1.0"));
        }

        [Test]
        public async Task ResolveAsync_NumericEvmChainId_Resolves()
        {
            var evmChain = new EvmChain { ChainId = 1, Name = "Ethereum Mainnet", RpcUrl = "https://eth.example.com", NativeCurrencySymbol = "ETH" };
            _evmChainRegistry.Setup(r => r.TryGetChainAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(evmChain);

            var resolved = await CreateResolver().ResolveAsync("1");

            Assert.That(resolved, Is.Not.Null);
            Assert.That(resolved!.Family, Is.EqualTo(ChainFamily.Evm));
            Assert.That(resolved.EvmChain!.ChainId, Is.EqualTo(1));
        }

        [Test]
        public async Task ResolveAsync_EvmNameFallback_Resolves()
        {
            var evmChain = new EvmChain { ChainId = 100, Name = "Gnosis", RpcUrl = "https://gnosis.example.com", NativeCurrencySymbol = "XDAI" };
            _evmChainRegistry.Setup(r => r.TryGetChainByNameAsync("Gnosis", It.IsAny<CancellationToken>())).ReturnsAsync(evmChain);

            var resolved = await CreateResolver().ResolveAsync("Gnosis");

            Assert.That(resolved, Is.Not.Null);
            Assert.That(resolved!.Family, Is.EqualTo(ChainFamily.Evm));
            Assert.That(resolved.EvmChain!.ChainId, Is.EqualTo(100));
        }

        [Test]
        public async Task ResolveAsync_UnresolvableNetwork_ReturnsNull()
        {
            _evmChainRegistry.Setup(r => r.TryGetChainByNameAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync((EvmChain?)null);

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
        public async Task ListNetworksAsync_ReturnsAvmChainsPlusLiveWellKnownEvmChains()
        {
            _evmChainRegistry.Setup(r => r.TryGetChainAsync(1, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new EvmChain { ChainId = 1, Name = "Ethereum Mainnet", RpcUrl = "https://eth.example.com", NativeCurrencySymbol = "ETH" });
            _evmChainRegistry.Setup(r => r.TryGetChainAsync(100, It.IsAny<CancellationToken>())).ReturnsAsync((EvmChain?)null);
            _evmChainRegistry.Setup(r => r.TryGetChainAsync(42161, It.IsAny<CancellationToken>())).ReturnsAsync((EvmChain?)null);
            _evmChainRegistry.Setup(r => r.TryGetChainAsync(8453, It.IsAny<CancellationToken>())).ReturnsAsync((EvmChain?)null);

            var summaries = await CreateResolver().ListNetworksAsync();

            Assert.That(summaries.Count(s => s.Family == "Avm"), Is.EqualTo(2));
            Assert.That(summaries.Count(s => s.Family == "Evm"), Is.EqualTo(1));
            Assert.That(summaries.Single(s => s.Family == "Evm").Id, Is.EqualTo("1"));
        }
    }
}
