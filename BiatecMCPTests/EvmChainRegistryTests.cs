using BiatecMCP.BusinessLogic;
using Microsoft.Extensions.Caching.Memory;
using Moq;

namespace BiatecMCPTests
{
    /// <summary>
    /// Covers <see cref="EvmChainRegistry"/>'s selection/caching logic against a mocked
    /// <see cref="IPublicEvmRpcDataSource"/> - no live HTTP. Unlike <see cref="AlgorandChainRegistry"/>
    /// (which eagerly verifies its whole small chain list), this registry resolves+verifies liveness lazily,
    /// per requested chain only - the public EVM chain list has thousands of entries, so eagerly checking
    /// all of them would mean thousands of speculative HTTP calls per cache refresh.
    /// </summary>
    [TestFixture]
    public class EvmChainRegistryTests
    {
        private Mock<IPublicEvmRpcDataSource> _dataSource = null!;
        private IMemoryCache _cache = null!;

        [SetUp]
        public void SetUp()
        {
            _dataSource = new Mock<IPublicEvmRpcDataSource>();
            _cache = new MemoryCache(new MemoryCacheOptions());
        }

        [TearDown]
        public void TearDown()
        {
            _cache.Dispose();
        }

        private EvmChainRegistry CreateRegistry() => new(_dataSource.Object, _cache);

        private static EvmChainListEntry Entry(string name, long chainId, string shortName, params string[] rpcCandidates) => new()
        {
            Name = name,
            ChainId = chainId,
            ShortName = shortName,
            NativeCurrencySymbol = "ETH",
            NativeCurrencyDecimals = 18,
            RpcCandidates = rpcCandidates.ToList()
        };

        [Test]
        public async Task TryGetChainAsync_UnknownChainId_ReturnsNull()
        {
            _dataSource.Setup(d => d.GetChainListAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<EvmChainListEntry>());

            var chain = await CreateRegistry().TryGetChainAsync(999999999);

            Assert.That(chain, Is.Null);
        }

        [Test]
        public async Task TryGetChainAsync_NoLiveRpc_ReturnsNull()
        {
            _dataSource.Setup(d => d.GetChainListAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new[] { Entry("Ethereum Mainnet", 1, "eth", "https://dead.example.com") });
            _dataSource.Setup(d => d.TryGetLiveChainIdAsync("https://dead.example.com", It.IsAny<CancellationToken>()))
                .ReturnsAsync((long?)null);

            var chain = await CreateRegistry().TryGetChainAsync(1);

            Assert.That(chain, Is.Null);
        }

        [Test]
        public async Task TryGetChainAsync_RpcReportsWrongChainId_ReturnsNull()
        {
            _dataSource.Setup(d => d.GetChainListAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new[] { Entry("Ethereum Mainnet", 1, "eth", "https://wrong-chain.example.com") });
            _dataSource.Setup(d => d.TryGetLiveChainIdAsync("https://wrong-chain.example.com", It.IsAny<CancellationToken>()))
                .ReturnsAsync(999L);

            var chain = await CreateRegistry().TryGetChainAsync(1);

            Assert.That(chain, Is.Null);
        }

        [Test]
        public async Task TryGetChainAsync_FirstLiveMatchingRpcWins()
        {
            _dataSource.Setup(d => d.GetChainListAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new[] { Entry("Ethereum Mainnet", 1, "eth", "https://dead.example.com", "https://live.example.com") });
            _dataSource.Setup(d => d.TryGetLiveChainIdAsync("https://dead.example.com", It.IsAny<CancellationToken>())).ReturnsAsync((long?)null);
            _dataSource.Setup(d => d.TryGetLiveChainIdAsync("https://live.example.com", It.IsAny<CancellationToken>())).ReturnsAsync(1L);

            var chain = await CreateRegistry().TryGetChainAsync(1);

            Assert.That(chain, Is.Not.Null);
            Assert.That(chain!.RpcUrl, Is.EqualTo("https://live.example.com"));
            Assert.That(chain.ChainId, Is.EqualTo(1));
        }

        [Test]
        public async Task TryGetChainAsync_ResultIsCached_SecondCallDoesNotRefetchChainList()
        {
            _dataSource.Setup(d => d.GetChainListAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new[] { Entry("Ethereum Mainnet", 1, "eth", "https://live.example.com") });
            _dataSource.Setup(d => d.TryGetLiveChainIdAsync("https://live.example.com", It.IsAny<CancellationToken>())).ReturnsAsync(1L);
            var registry = CreateRegistry();

            await registry.TryGetChainAsync(1);
            await registry.TryGetChainAsync(1);

            _dataSource.Verify(d => d.GetChainListAsync(It.IsAny<CancellationToken>()), Times.Once);
            _dataSource.Verify(d => d.TryGetLiveChainIdAsync("https://live.example.com", It.IsAny<CancellationToken>()), Times.Once);
        }

        [Test]
        public async Task TryGetChainByNameAsync_ExactName_Resolves()
        {
            _dataSource.Setup(d => d.GetChainListAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new[] { Entry("Gnosis", 100, "gno", "https://live.example.com") });
            _dataSource.Setup(d => d.TryGetLiveChainIdAsync("https://live.example.com", It.IsAny<CancellationToken>())).ReturnsAsync(100L);

            var chain = await CreateRegistry().TryGetChainByNameAsync("Gnosis");

            Assert.That(chain, Is.Not.Null);
            Assert.That(chain!.ChainId, Is.EqualTo(100));
        }

        [Test]
        public async Task TryGetChainByNameAsync_NormalizedMainnetSuffix_Resolves()
        {
            _dataSource.Setup(d => d.GetChainListAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new[] { Entry("Ethereum Mainnet", 1, "eth", "https://live.example.com") });
            _dataSource.Setup(d => d.TryGetLiveChainIdAsync("https://live.example.com", It.IsAny<CancellationToken>())).ReturnsAsync(1L);

            var chain = await CreateRegistry().TryGetChainByNameAsync("Ethereum");

            Assert.That(chain, Is.Not.Null);
            Assert.That(chain!.ChainId, Is.EqualTo(1));
        }

        [Test]
        public async Task TryGetChainByNameAsync_NormalizedOneSuffix_Resolves()
        {
            _dataSource.Setup(d => d.GetChainListAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new[] { Entry("Arbitrum One", 42161, "arb1", "https://live.example.com") });
            _dataSource.Setup(d => d.TryGetLiveChainIdAsync("https://live.example.com", It.IsAny<CancellationToken>())).ReturnsAsync(42161L);

            var chain = await CreateRegistry().TryGetChainByNameAsync("Arbitrum");

            Assert.That(chain, Is.Not.Null);
            Assert.That(chain!.ChainId, Is.EqualTo(42161));
        }

        [Test]
        public async Task TryGetChainByNameAsync_UnknownName_ReturnsNull()
        {
            _dataSource.Setup(d => d.GetChainListAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new[] { Entry("Ethereum Mainnet", 1, "eth", "https://live.example.com") });

            var chain = await CreateRegistry().TryGetChainByNameAsync("NotARealChain");

            Assert.That(chain, Is.Null);
        }
    }
}
