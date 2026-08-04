namespace BiatecOIDC.BusinessLogic
{
    /// <inheritdoc cref="INetworkResolver"/>
    /// <remarks>
    /// Matches the exact same strict, closed <c>network</c> code vocabulary as BiatecMCP's own
    /// (independent, no-compile-time-coupling) <c>NetworkResolver</c> - since BiatecMCP's
    /// <c>signTransaction</c>/<c>executeTransaction</c> tools forward their own <c>network</c> parameter
    /// straight through to this service's <c>POST /wallet/{network}/{address}/sign</c> route, both sides
    /// must recognize the same codes or a value BiatecMCP considers valid would 400 here. See CLAUDE.md's
    /// "Strict network codes" note for the naming convention and the real reported confusion (an agent
    /// broadcasting a signed transaction to the wrong network) this replaced fuzzy matching to prevent.
    /// </remarks>
    public sealed class NetworkResolver : INetworkResolver
    {
        /// <summary>
        /// Pins the exact code for AVM chains whose live registry display name wouldn't already slugify to
        /// the intended code - in particular Algorand's own mainnet/testnet, which the dynamic
        /// <see cref="IAlgorandChainRegistry"/> registry may report just as <c>"Algorand"</c> (no "Mainnet"
        /// suffix). Any genesis id not listed here falls back to <see cref="SlugifyAvmName"/> on the chain's
        /// own reported name - kept in sync with BiatecMCP's own copy of this table, since the same code
        /// must resolve to the same chain family on both sides of a <c>signTransaction</c>/
        /// <c>executeTransaction</c> call.
        /// </summary>
        private static readonly Dictionary<string, string> KnownAvmCodesByGenesisId = new(StringComparer.OrdinalIgnoreCase)
        {
            ["mainnet-v1.0"] = "algorand-mainnet",
            ["testnet-v1.0"] = "algorand-testnet",
            ["voimain-v1.0"] = "voi-mainnet"
        };

        /// <summary>
        /// Recognized purely so an EVM network code in a wallet route gets a clean "not supported yet"
        /// response instead of "unknown network" - BiatecOIDC never discovers/talks to a live EVM chain
        /// itself (see <see cref="ResolvedNetwork"/>'s remarks), so this is a closed code check only, the
        /// same four codes BiatecMCP's own resolver's <c>WellKnownEvmChains</c> table exposes.
        /// </summary>
        private static readonly string[] WellKnownEvmCodes = ["ethereum", "gnosis", "arbitrum", "base"];

        private const string BitcoinCode = "bitcoin";
        private const string BitcoinCashCode = "bitcoin-cash";

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
            var avmChain = avmChains.FirstOrDefault(c => string.Equals(ResolveAvmCode(c.GenesisId, c.Name), network, StringComparison.OrdinalIgnoreCase));
            if (avmChain != null)
            {
                return new ResolvedNetwork { Family = ChainFamily.Avm, DisplayName = avmChain.Name, AvmChain = avmChain };
            }

            if (WellKnownEvmCodes.Any(code => string.Equals(code, network, StringComparison.OrdinalIgnoreCase)))
            {
                return new ResolvedNetwork { Family = ChainFamily.Evm, DisplayName = network };
            }

            if (string.Equals(BitcoinCode, network, StringComparison.OrdinalIgnoreCase))
            {
                return new ResolvedNetwork { Family = ChainFamily.Btc, DisplayName = "Bitcoin" };
            }

            if (string.Equals(BitcoinCashCode, network, StringComparison.OrdinalIgnoreCase))
            {
                return new ResolvedNetwork { Family = ChainFamily.Bch, DisplayName = "Bitcoin Cash" };
            }

            return null;
        }

        private static string ResolveAvmCode(string genesisId, string fallbackName) =>
            KnownAvmCodesByGenesisId.TryGetValue(genesisId, out var code) ? code : SlugifyAvmName(fallbackName);

        /// <summary>
        /// Lowercases and hyphen-joins a chain's display name (e.g. <c>"Aramid Mainnet"</c> →
        /// <c>"aramid-mainnet"</c>) - the generic fallback for any AVM chain not in
        /// <see cref="KnownAvmCodesByGenesisId"/>. Kept byte-for-byte identical to BiatecMCP's own copy so
        /// both sides always agree on the code for a newly-added public AVM chain.
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
