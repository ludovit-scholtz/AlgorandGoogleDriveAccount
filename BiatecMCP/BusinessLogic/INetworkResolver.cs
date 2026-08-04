namespace BiatecMCP.BusinessLogic
{
    /// <summary>Which blockchain "family" a resolved network belongs to.</summary>
    public enum ChainFamily
    {
        /// <summary>Algorand Virtual Machine - Algorand, Voi, Aramid, and any other AVM-compatible chain.</summary>
        Avm,

        /// <summary>Ethereum Virtual Machine - Ethereum, Gnosis, Arbitrum, Base, and any other EVM-compatible chain.</summary>
        Evm,

        /// <summary>Bitcoin mainnet.</summary>
        Btc,

        /// <summary>Bitcoin Cash mainnet.</summary>
        Bch
    }

    /// <summary>One network a <c>network</c> tool parameter successfully resolved to.</summary>
    public sealed class ResolvedNetwork
    {
        /// <summary>
        /// The canonical code this network resolved from (e.g. <c>"algorand-mainnet"</c>) - always exactly
        /// one of <see cref="INetworkResolver.ListNetworksAsync"/>'s <see cref="NetworkSummary.Code"/>
        /// values, since <see cref="INetworkResolver.ResolveAsync"/> only ever matches against that closed
        /// set (see its own remarks for why).
        /// </summary>
        public string Code { get; set; } = string.Empty;

        public ChainFamily Family { get; set; }
        public string DisplayName { get; set; } = string.Empty;

        /// <summary>Set only when <see cref="Family"/> is <see cref="ChainFamily.Avm"/>.</summary>
        public AlgorandChain? AvmChain { get; set; }

        /// <summary>
        /// A locally-configured <c>Algod:Networks</c> entry's own explorer URL override, if this network
        /// came from one and it set one - takes precedence over <c>BiatecMCP.MCP.BiatecMCP</c>'s
        /// <c>KnownExplorerBaseUrls</c> table, so an operator can still point at a custom/self-hosted
        /// explorer per network.
        /// </summary>
        public string? ConfiguredExplorerBaseUrlOverride { get; set; }

        /// <summary>Set only when <see cref="Family"/> is <see cref="ChainFamily.Evm"/>.</summary>
        public EvmChain? EvmChain { get; set; }
    }

    /// <summary>One network as listed by <see cref="INetworkResolver.ListNetworksAsync"/>.</summary>
    public sealed class NetworkSummary
    {
        /// <summary>
        /// The exact, case-insensitive string every <c>network</c> tool parameter expects for this chain -
        /// e.g. <c>"algorand-mainnet"</c>, <c>"algorand-testnet"</c>, <c>"voi-mainnet"</c>,
        /// <c>"aramid-mainnet"</c>, <c>"base"</c>, <c>"arbitrum"</c>, <c>"bitcoin"</c>,
        /// <c>"bitcoin-cash"</c>. See <see cref="NetworkResolver"/>'s remarks for the naming convention.
        /// </summary>
        public string Code { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;
        public string Family { get; set; } = string.Empty;

        /// <summary>The Algorand genesis id, or the EVM chain id as a string.</summary>
        public string Id { get; set; } = string.Empty;

        public string NativeCurrencySymbol { get; set; } = string.Empty;
    }

    /// <summary>
    /// Resolves a <c>network</c> tool parameter to a concrete, currently-live chain in either the
    /// Algorand-family (<see cref="IAlgorandChainRegistry"/>) or Ethereum-family
    /// (<see cref="IEvmChainRegistry"/>) registry, plus Bitcoin/Bitcoin Cash - see
    /// <see cref="NetworkResolver"/>. <see cref="ResolveAsync"/> only matches against the exact,
    /// case-insensitive canonical codes <see cref="ListNetworksAsync"/> itself returns - no fuzzy display-name
    /// matching, no raw genesis id, no numeric EVM chain id. This is deliberate: every
    /// <c>create*</c>/<c>signTransaction</c>/<c>submitTransactionToBlockchain</c> tool shares this one resolver, so a
    /// connected AI agent that gets <c>network</c> wrong for one tool gets it wrong for all of them the same
    /// way, and a single clear, closed vocabulary (with an error that names every valid code) is much less
    /// likely to be misused than an open-ended, fuzzy-matched one - see the "Unified broadcast tool" /
    /// "Strict network codes" architecture notes for the real reported confusion this fixes (an agent
    /// broadcasting a signed transaction to the wrong network entirely).
    /// </summary>
    public interface INetworkResolver
    {
        Task<ResolvedNetwork?> ResolveAsync(string network, CancellationToken cancellationToken = default);

        /// <summary>Every currently-supported network code, for tool/documentation discovery and for building "unknown network" error messages.</summary>
        Task<IReadOnlyList<NetworkSummary>> ListNetworksAsync(CancellationToken cancellationToken = default);
    }
}
