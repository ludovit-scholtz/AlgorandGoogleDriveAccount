using BiatecOIDC.BusinessLogic;
using Moq;

namespace BiatecOIDCTests
{
    /// <summary>
    /// Covers <see cref="NetworkResolver"/>'s AVM name/genesis-id matching (via a mocked
    /// <see cref="IAlgorandChainRegistry"/>) and its lightweight EVM name recognition - no live HTTP.
    /// </summary>
    [TestFixture]
    public class NetworkResolverTests
    {
        private Mock<IAlgorandChainRegistry> _algorandChainRegistry = null!;

        [SetUp]
        public void SetUp()
        {
            _algorandChainRegistry = new Mock<IAlgorandChainRegistry>();
            _algorandChainRegistry.Setup(r => r.GetSupportedChainsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new[]
            {
                new AlgorandChain { GenesisId = "mainnet-v1.0", Name = "Algorand Mainnet", GenesisHash = "H1", AlgodApiAddress = "https://mainnet.example.com" },
                new AlgorandChain { GenesisId = "voimain-v1.0", Name = "Voi Mainnet", GenesisHash = "H2", AlgodApiAddress = "https://voi.example.com" }
            });
        }

        private NetworkResolver CreateResolver() => new(_algorandChainRegistry.Object);

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
            Assert.That(resolved!.AvmChain!.GenesisId, Is.EqualTo("mainnet-v1.0"));
        }

        [Test]
        public async Task ResolveAsync_EvmWellKnownName_RecognizedWithNoAvmChain()
        {
            var resolved = await CreateResolver().ResolveAsync("Arbitrum");

            Assert.That(resolved, Is.Not.Null);
            Assert.That(resolved!.Family, Is.EqualTo(ChainFamily.Evm));
            Assert.That(resolved.AvmChain, Is.Null);
        }

        [Test]
        public async Task ResolveAsync_EvmFullName_Recognized()
        {
            var resolved = await CreateResolver().ResolveAsync("Ethereum Mainnet");

            Assert.That(resolved, Is.Not.Null);
            Assert.That(resolved!.Family, Is.EqualTo(ChainFamily.Evm));
        }

        [Test]
        public async Task ResolveAsync_UnknownNetwork_ReturnsNull()
        {
            var resolved = await CreateResolver().ResolveAsync("NotARealNetwork");

            Assert.That(resolved, Is.Null);
        }

        [Test]
        public async Task ResolveAsync_BlankNetwork_ReturnsNull()
        {
            var resolved = await CreateResolver().ResolveAsync("   ");

            Assert.That(resolved, Is.Null);
        }
    }
}
