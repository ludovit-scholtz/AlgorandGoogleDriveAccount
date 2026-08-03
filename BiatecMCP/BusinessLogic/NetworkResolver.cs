using System.Globalization;
using Microsoft.Extensions.Options;

namespace BiatecMCP.BusinessLogic
{
    /// <inheritdoc cref="INetworkResolver"/>
    public sealed class NetworkResolver : INetworkResolver
    {
        private static readonly string[] NameSuffixesToStrip = [" Mainnet"];

        /// <summary>Highlighted for <see cref="ListNetworksAsync"/> only - <see cref="ResolveAsync"/> can
        /// resolve any other public EVM chain by name or numeric id too, via <see cref="IEvmChainRegistry.TryGetChainByNameAsync"/>.</summary>
        private static readonly (string Name, long ChainId)[] WellKnownEvmChains =
        [
            ("Ethereum", 1),
            ("Gnosis", 100),
            ("Arbitrum One", 42161),
            ("Base", 8453)
        ];

        private readonly IAlgorandChainRegistry _algorandChainRegistry;
        private readonly IEvmChainRegistry _evmChainRegistry;
        private readonly IOptionsMonitor<Model.AlgodConfiguration> _algodConfig;

        public NetworkResolver(IAlgorandChainRegistry algorandChainRegistry, IEvmChainRegistry evmChainRegistry, IOptionsMonitor<Model.AlgodConfiguration> algodConfig)
        {
            _algorandChainRegistry = algorandChainRegistry;
            _evmChainRegistry = evmChainRegistry;
            _algodConfig = algodConfig;
        }

        public async Task<ResolvedNetwork?> ResolveAsync(string network, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(network))
            {
                return null;
            }

            // Locally-configured Algod:Networks entries win first - same "operator override" precedence
            // GetAlgodSettings already applies for every other genesisId-accepting tool.
            var localMatch = TryResolveLocalAvmConfig(network);
            if (localMatch != null)
            {
                return localMatch;
            }

            var avmChains = await _algorandChainRegistry.GetSupportedChainsAsync(cancellationToken);
            var avmChain = avmChains.FirstOrDefault(c => string.Equals(c.GenesisId, network, StringComparison.OrdinalIgnoreCase))
                ?? avmChains.FirstOrDefault(c => string.Equals(c.Name, network, StringComparison.OrdinalIgnoreCase))
                ?? avmChains.FirstOrDefault(c => string.Equals(NormalizeName(c.Name), NormalizeName(network), StringComparison.OrdinalIgnoreCase));
            if (avmChain != null)
            {
                return new ResolvedNetwork { Family = ChainFamily.Avm, DisplayName = avmChain.Name, AvmChain = avmChain };
            }

            if (long.TryParse(network, NumberStyles.Integer, CultureInfo.InvariantCulture, out var chainId))
            {
                var evmChainById = await _evmChainRegistry.TryGetChainAsync(chainId, cancellationToken);
                if (evmChainById != null)
                {
                    return new ResolvedNetwork { Family = ChainFamily.Evm, DisplayName = evmChainById.Name, EvmChain = evmChainById };
                }
            }

            var evmChainByName = await _evmChainRegistry.TryGetChainByNameAsync(network, cancellationToken);
            if (evmChainByName != null)
            {
                return new ResolvedNetwork { Family = ChainFamily.Evm, DisplayName = evmChainByName.Name, EvmChain = evmChainByName };
            }

            return null;
        }

        public async Task<IReadOnlyList<NetworkSummary>> ListNetworksAsync(CancellationToken cancellationToken = default)
        {
            var summaries = new List<NetworkSummary>();

            var avmChains = await _algorandChainRegistry.GetSupportedChainsAsync(cancellationToken);
            summaries.AddRange(avmChains.Select(c => new NetworkSummary
            {
                Name = c.Name,
                Family = nameof(ChainFamily.Avm),
                Id = c.GenesisId,
                NativeCurrencySymbol = "ALGO"
            }));

            foreach (var (name, chainId) in WellKnownEvmChains)
            {
                var evmChain = await _evmChainRegistry.TryGetChainAsync(chainId, cancellationToken);
                if (evmChain != null)
                {
                    summaries.Add(new NetworkSummary
                    {
                        Name = name,
                        Family = nameof(ChainFamily.Evm),
                        Id = chainId.ToString(CultureInfo.InvariantCulture),
                        NativeCurrencySymbol = evmChain.NativeCurrencySymbol
                    });
                }
            }

            return summaries;
        }

        private ResolvedNetwork? TryResolveLocalAvmConfig(string network)
        {
            if (!_algodConfig.CurrentValue.Networks.TryGetValue(network.ToLowerInvariant(), out var settings))
            {
                return null;
            }

            return new ResolvedNetwork
            {
                Family = ChainFamily.Avm,
                DisplayName = network,
                AvmChain = new AlgorandChain
                {
                    GenesisId = network,
                    AlgodApiAddress = settings.ApiAddress,
                    AlgodApiToken = settings.ApiToken
                }
            };
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
