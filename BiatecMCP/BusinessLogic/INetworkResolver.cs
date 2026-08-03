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
        public ChainFamily Family { get; set; }
        public string DisplayName { get; set; } = string.Empty;

        /// <summary>Set only when <see cref="Family"/> is <see cref="ChainFamily.Avm"/>.</summary>
        public AlgorandChain? AvmChain { get; set; }

        /// <summary>Set only when <see cref="Family"/> is <see cref="ChainFamily.Evm"/>.</summary>
        public EvmChain? EvmChain { get; set; }
    }

    /// <summary>One network as listed by <see cref="INetworkResolver.ListNetworksAsync"/>.</summary>
    public sealed class NetworkSummary
    {
        public string Name { get; set; } = string.Empty;
        public string Family { get; set; } = string.Empty;

        /// <summary>The Algorand genesis id, or the EVM chain id as a string.</summary>
        public string Id { get; set; } = string.Empty;

        public string NativeCurrencySymbol { get; set; } = string.Empty;
    }

    /// <summary>
    /// Resolves a human-supplied <c>network</c> string (e.g. <c>"Algorand"</c>, <c>"Voi"</c>,
    /// <c>"Ethereum"</c>, <c>"Arbitrum"</c>, or a raw genesis id / numeric EVM chain id) to a concrete,
    /// currently-live chain in either the Algorand-family (<see cref="IAlgorandChainRegistry"/>) or
    /// Ethereum-family (<see cref="IEvmChainRegistry"/>) registry - see <see cref="NetworkResolver"/>.
    /// </summary>
    public interface INetworkResolver
    {
        Task<ResolvedNetwork?> ResolveAsync(string network, CancellationToken cancellationToken = default);

        /// <summary>
        /// Every currently-live AVM chain plus a small set of well-known EVM chains, for tool/documentation
        /// discovery - not the full public EVM chain universe (thousands of entries), which
        /// <see cref="ResolveAsync"/> can still resolve by name or numeric chain id even though it isn't
        /// listed here.
        /// </summary>
        Task<IReadOnlyList<NetworkSummary>> ListNetworksAsync(CancellationToken cancellationToken = default);
    }
}
