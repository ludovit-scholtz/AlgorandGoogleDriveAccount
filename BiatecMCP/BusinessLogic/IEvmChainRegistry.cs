namespace BiatecMCP.BusinessLogic
{
    /// <summary>
    /// Resolves EVM (Ethereum-family) chains from the public chain/RPC registry
    /// (https://chainid.network/chains.json), verifying liveness lazily - only for the specific chain a
    /// caller asks about, never the whole ~2,700-chain list at once (unlike
    /// <see cref="IAlgorandChainRegistry"/>'s eager approach, which is affordable only because the
    /// Algorand-family list is tiny). See <see cref="EvmChainRegistry"/>.
    /// </summary>
    public interface IEvmChainRegistry
    {
        /// <summary>Looks up a chain by its numeric chain id (e.g. <c>1</c> for Ethereum). Returns <c>null</c> if not currently reachable via any published RPC.</summary>
        Task<EvmChain?> TryGetChainAsync(long chainId, CancellationToken cancellationToken = default);

        /// <summary>
        /// Looks up a chain by name, normalizing a trailing " Mainnet"/" One" before comparing (so
        /// "Ethereum" matches "Ethereum Mainnet" and "Arbitrum" matches "Arbitrum One") - falls back to an
        /// exact name/short-name match. Returns <c>null</c> if not found or not currently reachable.
        /// </summary>
        Task<EvmChain?> TryGetChainByNameAsync(string name, CancellationToken cancellationToken = default);
    }
}
