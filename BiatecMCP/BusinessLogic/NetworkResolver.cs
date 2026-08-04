using System.Globalization;
using Microsoft.Extensions.Options;

namespace BiatecMCP.BusinessLogic
{
    /// <inheritdoc cref="INetworkResolver"/>
    /// <remarks>
    /// Canonical code naming convention: AVM chains are <c>{chain}-{variant}</c> (e.g.
    /// <c>"algorand-mainnet"</c>, <c>"algorand-testnet"</c>, <c>"voi-mainnet"</c>, <c>"aramid-mainnet"</c>) -
    /// lowercase, hyphen-separated, derived from the chain's own display name. EVM chains and Bitcoin-family
    /// chains are just <c>{chain}</c> (e.g. <c>"ethereum"</c>, <c>"arbitrum"</c>, <c>"base"</c>,
    /// <c>"bitcoin"</c>, <c>"bitcoin-cash"</c>) - no variant suffix, since this server doesn't distinguish
    /// EVM/Bitcoin testnets today. <see cref="KnownAvmCodesByGenesisId"/> pins the exact code for chains
    /// whose live display name wouldn't slugify to the intended code on its own (Algorand's own mainnet/
    /// testnet entries in particular); any other AVM chain the dynamic registry currently reports falls back
    /// to <see cref="SlugifyAvmName"/>, so a newly-added public AVM chain still gets a predictable code
    /// without needing a code change here first.
    /// </remarks>
    public sealed class NetworkResolver : INetworkResolver
    {
        /// <summary>
        /// Pins the exact code for AVM chains whose live registry display name might not already slugify to
        /// the intended code - in particular Algorand's own mainnet/testnet, which the dynamic
        /// <see cref="IAlgorandChainRegistry"/> registry may report just as <c>"Algorand"</c> (no "Mainnet"
        /// suffix). A genesis id here also resolves via a locally-configured <c>Algod:Networks</c> entry
        /// (see <see cref="TryResolveLocalAvmConfigByCode"/>), which always wins for connection details when
        /// present, whether or not the dynamic registry also currently reports that same chain live. Any
        /// genesis id not listed here falls back to <see cref="SlugifyAvmName"/> on the chain's own reported
        /// name.
        /// </summary>
        private static readonly Dictionary<string, string> KnownAvmCodesByGenesisId = new(StringComparer.OrdinalIgnoreCase)
        {
            ["mainnet-v1.0"] = "algorand-mainnet",
            ["testnet-v1.0"] = "algorand-testnet",
            ["voimain-v1.0"] = "voi-mainnet"
        };

        /// <summary>
        /// The closed set of EVM chains this server resolves - unlike AVM (whose universe is the dynamic,
        /// liveness-verified genesis registry), EVM chains are enumerated explicitly here: the public EVM
        /// chain universe is thousands of entries, and this server deliberately doesn't try to give every
        /// one of them a predictable short code - <see cref="ResolveAsync"/> only accepts these exact codes.
        /// </summary>
        private static readonly (string Code, string Name, long ChainId)[] WellKnownEvmChains =
        [
            ("ethereum", "Ethereum", 1),
            ("gnosis", "Gnosis", 100),
            ("arbitrum", "Arbitrum One", 42161),
            ("base", "Base", 8453)
        ];

        private const string BitcoinCode = "bitcoin";
        private const string BitcoinCashCode = "bitcoin-cash";

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

            var networks = await ListNetworksAsync(cancellationToken);
            var match = networks.FirstOrDefault(n => string.Equals(n.Code, network, StringComparison.OrdinalIgnoreCase));
            if (match == null)
            {
                return null;
            }

            return match.Family switch
            {
                nameof(ChainFamily.Avm) => await ResolveAvmByCodeAsync(match.Code, cancellationToken),
                nameof(ChainFamily.Evm) => await ResolveEvmByCodeAsync(match.Code, cancellationToken),
                nameof(ChainFamily.Btc) => new ResolvedNetwork { Code = BitcoinCode, Family = ChainFamily.Btc, DisplayName = "Bitcoin" },
                nameof(ChainFamily.Bch) => new ResolvedNetwork { Code = BitcoinCashCode, Family = ChainFamily.Bch, DisplayName = "Bitcoin Cash" },
                _ => null
            };
        }

        public async Task<IReadOnlyList<NetworkSummary>> ListNetworksAsync(CancellationToken cancellationToken = default)
        {
            var summaries = new List<NetworkSummary>();

            // Locally-configured Algod:Networks entries (e.g. testnet) are listed too, alongside whatever
            // the dynamic registry currently reports live - deduplicated by genesis id, local config wins
            // (same "operator override" precedence GetAlgodSettings already applies).
            var seenGenesisIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (genesisId, settings) in _algodConfig.CurrentValue.Networks)
            {
                if (!seenGenesisIds.Add(genesisId))
                {
                    continue;
                }

                summaries.Add(new NetworkSummary
                {
                    Code = ResolveAvmCode(genesisId, genesisId),
                    Name = genesisId,
                    Family = nameof(ChainFamily.Avm),
                    Id = genesisId,
                    NativeCurrencySymbol = "ALGO"
                });
            }

            var avmChains = await _algorandChainRegistry.GetSupportedChainsAsync(cancellationToken);
            foreach (var chain in avmChains)
            {
                if (!seenGenesisIds.Add(chain.GenesisId))
                {
                    continue;
                }

                summaries.Add(new NetworkSummary
                {
                    Code = ResolveAvmCode(chain.GenesisId, chain.Name),
                    Name = chain.Name,
                    Family = nameof(ChainFamily.Avm),
                    Id = chain.GenesisId,
                    NativeCurrencySymbol = "ALGO"
                });
            }

            foreach (var (code, name, chainId) in WellKnownEvmChains)
            {
                var evmChain = await _evmChainRegistry.TryGetChainAsync(chainId, cancellationToken);
                if (evmChain != null)
                {
                    summaries.Add(new NetworkSummary
                    {
                        Code = code,
                        Name = name,
                        Family = nameof(ChainFamily.Evm),
                        Id = chainId.ToString(CultureInfo.InvariantCulture),
                        NativeCurrencySymbol = evmChain.NativeCurrencySymbol
                    });
                }
            }

            summaries.Add(new NetworkSummary { Code = BitcoinCode, Name = "Bitcoin", Family = nameof(ChainFamily.Btc), Id = BitcoinCode, NativeCurrencySymbol = "BTC" });
            summaries.Add(new NetworkSummary { Code = BitcoinCashCode, Name = "Bitcoin Cash", Family = nameof(ChainFamily.Bch), Id = BitcoinCashCode, NativeCurrencySymbol = "BCH" });

            return summaries;
        }

        private async Task<ResolvedNetwork?> ResolveAvmByCodeAsync(string code, CancellationToken cancellationToken)
        {
            if (TryResolveLocalAvmConfigByCode(code) is { } localMatch)
            {
                return localMatch;
            }

            var avmChains = await _algorandChainRegistry.GetSupportedChainsAsync(cancellationToken);
            var chain = avmChains.FirstOrDefault(c => string.Equals(ResolveAvmCode(c.GenesisId, c.Name), code, StringComparison.OrdinalIgnoreCase));
            return chain == null ? null : new ResolvedNetwork { Code = code, Family = ChainFamily.Avm, DisplayName = chain.Name, AvmChain = chain };
        }

        private async Task<ResolvedNetwork?> ResolveEvmByCodeAsync(string code, CancellationToken cancellationToken)
        {
            var known = WellKnownEvmChains.FirstOrDefault(c => string.Equals(c.Code, code, StringComparison.OrdinalIgnoreCase));
            if (known.Code == null)
            {
                return null;
            }

            var evmChain = await _evmChainRegistry.TryGetChainAsync(known.ChainId, cancellationToken);
            return evmChain == null ? null : new ResolvedNetwork { Code = code, Family = ChainFamily.Evm, DisplayName = evmChain.Name, EvmChain = evmChain };
        }

        private ResolvedNetwork? TryResolveLocalAvmConfigByCode(string code)
        {
            foreach (var (genesisId, settings) in _algodConfig.CurrentValue.Networks)
            {
                if (!string.Equals(ResolveAvmCode(genesisId, genesisId), code, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                return new ResolvedNetwork
                {
                    Code = code,
                    Family = ChainFamily.Avm,
                    DisplayName = genesisId,
                    AvmChain = new AlgorandChain
                    {
                        GenesisId = genesisId,
                        AlgodApiAddress = settings.ApiAddress,
                        AlgodApiToken = settings.ApiToken
                    },
                    ConfiguredExplorerBaseUrlOverride = string.IsNullOrEmpty(settings.ExplorerBaseUrl) ? null : settings.ExplorerBaseUrl
                };
            }

            return null;
        }

        private static string ResolveAvmCode(string genesisId, string fallbackName) =>
            KnownAvmCodesByGenesisId.TryGetValue(genesisId, out var code) ? code : SlugifyAvmName(fallbackName);

        /// <summary>
        /// Lowercases and hyphen-joins a chain's display name (e.g. <c>"Aramid Mainnet"</c> →
        /// <c>"aramid-mainnet"</c>, <c>"testnet-v1.0"</c> → <c>"testnet-v1-0"</c> for a genesis id used as a
        /// fallback name) - the generic fallback for any AVM chain not in <see cref="KnownAvmCodesByGenesisId"/>.
        /// </summary>
        private static string SlugifyAvmName(string name)
        {
            var chars = name.Trim().ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray();
            var slug = new string(chars);
            while (slug.Contains("--", StringComparison.Ordinal))
            {
                slug = slug.Replace("--", "-", StringComparison.Ordinal);
            }

            return slug.Trim('-');
        }
    }
}
