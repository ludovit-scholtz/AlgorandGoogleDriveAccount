namespace BiatecOIDC.BusinessLogic
{
    /// <inheritdoc cref="INetworkResolver"/>
    public sealed class NetworkResolver : INetworkResolver
    {
        private static readonly string[] NameSuffixesToStrip = [" Mainnet", " One"];

        /// <summary>
        /// Recognized purely so an EVM network name in a wallet route gets a clean "not supported yet"
        /// response instead of "unknown network" - BiatecOIDC never discovers/talks to a live EVM chain
        /// itself (see <see cref="ResolvedNetwork"/>'s remarks), so this is a name check only, same four
        /// names BiatecMCP's own resolver highlights.
        /// </summary>
        private static readonly string[] WellKnownEvmNames = ["Ethereum", "Ethereum Mainnet", "Gnosis", "Arbitrum", "Arbitrum One", "Base"];

        /// <summary>Recognized names for Bitcoin mainnet.</summary>
        private static readonly string[] BitcoinNames = ["Bitcoin", "BTC"];

        /// <summary>Recognized names for Bitcoin Cash mainnet.</summary>
        private static readonly string[] BitcoinCashNames = ["BitcoinCash", "Bitcoin Cash", "BCH"];

        private readonly IAlgorandChainRegistry _algorandChainRegistry;

        public NetworkResolver(IAlgorandChainRegistry algorandChainRegistry)
        {
            _algorandChainRegistry = algorandChainRegistry;
        }

        public async Task<ResolvedNetwork?> ResolveAsync(string network, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(network))
            {
                return null;
            }

            var avmChains = await _algorandChainRegistry.GetSupportedChainsAsync(cancellationToken);
            var avmChain = avmChains.FirstOrDefault(c => string.Equals(c.GenesisId, network, StringComparison.OrdinalIgnoreCase))
                ?? avmChains.FirstOrDefault(c => string.Equals(c.Name, network, StringComparison.OrdinalIgnoreCase))
                ?? avmChains.FirstOrDefault(c => string.Equals(NormalizeName(c.Name), NormalizeName(network), StringComparison.OrdinalIgnoreCase));
            if (avmChain != null)
            {
                return new ResolvedNetwork { Family = ChainFamily.Avm, DisplayName = avmChain.Name, AvmChain = avmChain };
            }

            if (WellKnownEvmNames.Any(n => string.Equals(n, network, StringComparison.OrdinalIgnoreCase))
                || WellKnownEvmNames.Any(n => string.Equals(NormalizeName(n), NormalizeName(network), StringComparison.OrdinalIgnoreCase)))
            {
                return new ResolvedNetwork { Family = ChainFamily.Evm, DisplayName = network };
            }

            if (BitcoinNames.Any(n => string.Equals(n, network, StringComparison.OrdinalIgnoreCase)))
            {
                return new ResolvedNetwork { Family = ChainFamily.Btc, DisplayName = "Bitcoin" };
            }

            if (BitcoinCashNames.Any(n => string.Equals(n, network, StringComparison.OrdinalIgnoreCase)))
            {
                return new ResolvedNetwork { Family = ChainFamily.Bch, DisplayName = "Bitcoin Cash" };
            }

            return null;
        }

        private static string NormalizeName(string name)
        {
            var trimmed = name.Trim();
            foreach (var suffix in NameSuffixesToStrip)
            {
                if (trimmed.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    return trimmed[..^suffix.Length];
                }
            }

            return trimmed;
        }
    }
}
