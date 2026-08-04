using BiatecOIDC.BusinessLogic;
using Moq;

namespace BiatecOIDCTests
{
    /// <summary>
    /// Covers <see cref="NetworkResolver"/>'s strict canonical-code resolution (via a mocked
    /// <see cref="IAlgorandChainRegistry"/>) - <see cref="NetworkResolver.ResolveAsync"/> only matches the
    /// exact (case-insensitive) codes documented in CLAUDE.md's "Strict network codes" note, kept in sync
    /// with BiatecMCP's own independent copy of this resolver - no live HTTP.
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
                new AlgorandChain { GenesisId = "mainnet-v1.0", Name = "Algorand", GenesisHash = "H1", AlgodApiAddress = "https://mainnet.example.com" },
                new AlgorandChain { GenesisId = "testnet-v1.0", Name = "Algorand Testnet", GenesisHash = "H2", AlgodApiAddress = "https://testnet.example.com" },
                new AlgorandChain { GenesisId = "voimain-v1.0", Name = "Voi Mainnet", GenesisHash = "H3", AlgodApiAddress = "https://voi.example.com" },
                new AlgorandChain { GenesisId = "aramidmain-v1.0", Name = "Aramid Mainnet", GenesisHash = "H4", AlgodApiAddress = "https://aramid.example.com" }
            });
        }

        private NetworkResolver CreateResolver() => new(_algorandChainRegistry.Object);

        [Test]
        public async Task ResolveAsync_AlgorandMainnetCode_Resolves()
        {
            var resolved = await CreateResolver().ResolveAsync("algorand-mainnet");

            Assert.That(resolved, Is.Not.Null);
            Assert.That(resolved!.Family, Is.EqualTo(ChainFamily.Avm));
            Assert.That(resolved.AvmChain!.GenesisId, Is.EqualTo("mainnet-v1.0"));
        }

        [Test]
        public async Task ResolveAsync_AlgorandTestnetCode_Resolves()
        {
            var resolved = await CreateResolver().ResolveAsync("algorand-testnet");

            Assert.That(resolved, Is.Not.Null);
            Assert.That(resolved!.AvmChain!.GenesisId, Is.EqualTo("testnet-v1.0"));
        }

        [Test]
        public async Task ResolveAsync_VoiMainnetCode_Resolves()
        {
            var resolved = await CreateResolver().ResolveAsync("voi-mainnet");

            Assert.That(resolved, Is.Not.Null);
            Assert.That(resolved!.AvmChain!.GenesisId, Is.EqualTo("voimain-v1.0"));
        }

        [Test]
        public async Task ResolveAsync_ChainNotInKnownTable_SlugifiesFromLiveDisplayName()
        {
            var resolved = await CreateResolver().ResolveAsync("aramid-mainnet");

            Assert.That(resolved, Is.Not.Null);
            Assert.That(resolved!.AvmChain!.GenesisId, Is.EqualTo("aramidmain-v1.0"));
        }

        [Test]
        public async Task ResolveAsync_CodeIsCaseInsensitive()
        {
            var resolved = await CreateResolver().ResolveAsync("Algorand-Mainnet");

            Assert.That(resolved, Is.Not.Null);
            Assert.That(resolved!.AvmChain!.GenesisId, Is.EqualTo("mainnet-v1.0"));
        }

        [Test]
        public async Task ResolveAsync_GenesisIdNoLongerAccepted_ReturnsNull()
        {
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
        public async Task ResolveAsync_EvmWellKnownCode_RecognizedWithNoAvmChain()
        {
            var resolved = await CreateResolver().ResolveAsync("arbitrum");

            Assert.That(resolved, Is.Not.Null);
            Assert.That(resolved!.Family, Is.EqualTo(ChainFamily.Evm));
            Assert.That(resolved.AvmChain, Is.Null);
        }

        [Test]
        public async Task ResolveAsync_EvmFullNameNoLongerAccepted_ReturnsNull()
        {
            var resolved = await CreateResolver().ResolveAsync("Ethereum Mainnet");

            Assert.That(resolved, Is.Null);
        }

        [Test]
        public async Task ResolveAsync_BitcoinCode_Resolves()
        {
            var resolved = await CreateResolver().ResolveAsync("bitcoin");

            Assert.That(resolved, Is.Not.Null);
            Assert.That(resolved!.Family, Is.EqualTo(ChainFamily.Btc));
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
