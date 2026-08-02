using BiatecMCP.BusinessLogic;
using Microsoft.Extensions.Caching.Memory;
using Moq;

namespace BiatecMCPTests
{
    /// <summary>
    /// Covers <see cref="AlgorandChainRegistry"/>'s selection/caching logic against a mocked
    /// <see cref="IPublicAlgodDataSource"/> - no live HTTP. A chain only counts as "supported" if at least
    /// one of its published providers is currently reachable and reports the matching genesis hash; the
    /// real HTTP-calling implementation (<see cref="PublicAlgodDataSource"/>) is exercised manually/E2E,
    /// same precedent as this repo's other leaf HTTP providers (e.g. <c>FolksRouterQuoteProvider</c>).
    /// </summary>
    [TestFixture]
    public class AlgorandChainRegistryTests
    {
        private Mock<IPublicAlgodDataSource> _dataSource = null!;
        private IMemoryCache _cache = null!;

        [SetUp]
        public void SetUp()
        {
            _dataSource = new Mock<IPublicAlgodDataSource>();
            _cache = new MemoryCache(new MemoryCacheOptions());
        }

        [TearDown]
        public void TearDown()
        {
            _cache.Dispose();
        }

        private AlgorandChainRegistry CreateRegistry() => new(_dataSource.Object, _cache);

        private static GenesisListEntry Entry(string network, string genesisHash, string? chainId = null) => new()
        {
            Name = network,
            Network = network,
            ChainId = chainId,
            GenesisHash = genesisHash
        };

        private static PublicAlgodProvider Provider(string host, string token = "") => new()
        {
            ProviderName = host,
            AlgodHost = host,
            Header = "X-Algo-API-Token",
            Token = token
        };

        [Test]
        public async Task GetSupportedChainsAsync_SkipsEntriesWithBlankGenesisHash()
        {
            _dataSource.Setup(d => d.GetGenesisListAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new[] { Entry("sandnet-v1", "") });

            var chains = await CreateRegistry().GetSupportedChainsAsync();

            Assert.That(chains, Is.Empty);
            _dataSource.Verify(d => d.GetProvidersAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Test]
        public async Task GetSupportedChainsAsync_NoLiveProvider_ChainIsDropped()
        {
            _dataSource.Setup(d => d.GetGenesisListAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new[] { Entry("testnet-v1.0", "EXPECTEDHASH") });
            _dataSource.Setup(d => d.GetProvidersAsync("testnet-v1.0", It.IsAny<CancellationToken>()))
                .ReturnsAsync(new[] { Provider("https://dead.example.com") });
            _dataSource.Setup(d => d.TryGetLiveGenesisHashAsync(It.IsAny<PublicAlgodProvider>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string?)null);

            var chains = await CreateRegistry().GetSupportedChainsAsync();

            Assert.That(chains, Is.Empty);
        }

        [Test]
        public async Task GetSupportedChainsAsync_MismatchedGenesisHash_ChainIsDropped()
        {
            _dataSource.Setup(d => d.GetGenesisListAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new[] { Entry("testnet-v1.0", "EXPECTEDHASH") });
            _dataSource.Setup(d => d.GetProvidersAsync("testnet-v1.0", It.IsAny<CancellationToken>()))
                .ReturnsAsync(new[] { Provider("https://wrong-network.example.com") });
            _dataSource.Setup(d => d.TryGetLiveGenesisHashAsync(It.IsAny<PublicAlgodProvider>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync("SOMEOTHERHASH");

            var chains = await CreateRegistry().GetSupportedChainsAsync();

            Assert.That(chains, Is.Empty);
        }

        [Test]
        public async Task GetSupportedChainsAsync_FirstLiveMatchingProviderWins()
        {
            _dataSource.Setup(d => d.GetGenesisListAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new[] { Entry("mainnet-v1.0", "EXPECTEDHASH") });
            var deadProvider = Provider("https://dead.example.com");
            var liveProvider = Provider("https://live.example.com");
            _dataSource.Setup(d => d.GetProvidersAsync("mainnet-v1.0", It.IsAny<CancellationToken>()))
                .ReturnsAsync(new[] { deadProvider, liveProvider });
            _dataSource.Setup(d => d.TryGetLiveGenesisHashAsync(deadProvider, It.IsAny<CancellationToken>())).ReturnsAsync((string?)null);
            _dataSource.Setup(d => d.TryGetLiveGenesisHashAsync(liveProvider, It.IsAny<CancellationToken>())).ReturnsAsync("EXPECTEDHASH");

            var chains = await CreateRegistry().GetSupportedChainsAsync();

            Assert.That(chains, Has.Count.EqualTo(1));
            Assert.That(chains[0].AlgodApiAddress, Is.EqualTo("https://live.example.com"));
        }

        [Test]
        public async Task GetSupportedChainsAsync_ProvidersFetchThrowsForOneChain_OthersStillResolve()
        {
            _dataSource.Setup(d => d.GetGenesisListAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new[] { Entry("broken-v1.0", "HASH1"), Entry("mainnet-v1.0", "HASH2") });
            _dataSource.Setup(d => d.GetProvidersAsync("broken-v1.0", It.IsAny<CancellationToken>()))
                .ThrowsAsync(new HttpRequestException("unreachable"));
            var provider = Provider("https://live.example.com");
            _dataSource.Setup(d => d.GetProvidersAsync("mainnet-v1.0", It.IsAny<CancellationToken>()))
                .ReturnsAsync(new[] { provider });
            _dataSource.Setup(d => d.TryGetLiveGenesisHashAsync(provider, It.IsAny<CancellationToken>())).ReturnsAsync("HASH2");

            var chains = await CreateRegistry().GetSupportedChainsAsync();

            Assert.That(chains, Has.Count.EqualTo(1));
            Assert.That(chains[0].GenesisId, Is.EqualTo("mainnet-v1.0"));
        }

        [Test]
        public async Task GetSupportedChainsAsync_ResultIsCached_SecondCallDoesNotRefetchGenesisList()
        {
            _dataSource.Setup(d => d.GetGenesisListAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<GenesisListEntry>());
            var registry = CreateRegistry();

            await registry.GetSupportedChainsAsync();
            await registry.GetSupportedChainsAsync();

            _dataSource.Verify(d => d.GetGenesisListAsync(It.IsAny<CancellationToken>()), Times.Once);
        }

        [Test]
        public async Task TryGetChainAsync_ReturnsMatchingGenesisId()
        {
            _dataSource.Setup(d => d.GetGenesisListAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new[] { Entry("mainnet-v1.0", "HASH") });
            var provider = Provider("https://live.example.com");
            _dataSource.Setup(d => d.GetProvidersAsync("mainnet-v1.0", It.IsAny<CancellationToken>())).ReturnsAsync(new[] { provider });
            _dataSource.Setup(d => d.TryGetLiveGenesisHashAsync(provider, It.IsAny<CancellationToken>())).ReturnsAsync("HASH");

            var chain = await CreateRegistry().TryGetChainAsync("mainnet-v1.0");

            Assert.That(chain, Is.Not.Null);
            Assert.That(chain!.GenesisId, Is.EqualTo("mainnet-v1.0"));
        }

        [Test]
        public async Task TryGetChainAsync_UnknownGenesisId_ReturnsNull()
        {
            _dataSource.Setup(d => d.GetGenesisListAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<GenesisListEntry>());

            var chain = await CreateRegistry().TryGetChainAsync("nonexistent-v1.0");

            Assert.That(chain, Is.Null);
        }

        [Test]
        public async Task TryGetChainByAramidIdAsync_ReturnsMatchingChain()
        {
            _dataSource.Setup(d => d.GetGenesisListAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new[] { Entry("voimain-v1.0", "HASH", chainId: "416101") });
            var provider = Provider("https://live.example.com");
            _dataSource.Setup(d => d.GetProvidersAsync("voimain-v1.0", It.IsAny<CancellationToken>())).ReturnsAsync(new[] { provider });
            _dataSource.Setup(d => d.TryGetLiveGenesisHashAsync(provider, It.IsAny<CancellationToken>())).ReturnsAsync("HASH");

            var chain = await CreateRegistry().TryGetChainByAramidIdAsync(416101);

            Assert.That(chain, Is.Not.Null);
            Assert.That(chain!.GenesisId, Is.EqualTo("voimain-v1.0"));
        }

        [Test]
        public async Task TryGetChainByAramidIdAsync_NonNumericChainId_NeverMatches()
        {
            _dataSource.Setup(d => d.GetGenesisListAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new[] { Entry("sandnet-v1", "HASH", chainId: "SandBox") });
            var provider = Provider("https://live.example.com");
            _dataSource.Setup(d => d.GetProvidersAsync("sandnet-v1", It.IsAny<CancellationToken>())).ReturnsAsync(new[] { provider });
            _dataSource.Setup(d => d.TryGetLiveGenesisHashAsync(provider, It.IsAny<CancellationToken>())).ReturnsAsync("HASH");

            var chain = await CreateRegistry().TryGetChainByAramidIdAsync(0);

            Assert.That(chain, Is.Null);
        }
    }
}
