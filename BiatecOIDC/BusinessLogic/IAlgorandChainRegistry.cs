namespace BiatecOIDC.BusinessLogic
{
    /// <summary>
    /// The set of Algorand-family chains BiatecOIDC currently knows to be usable - every chain in the public
    /// genesis registry (https://scholtz.github.io/AlgorandPublicData) that also has at least one publicly
    /// reachable algod node reporting the right genesis hash right now. A chain that's merely listed but
    /// unreachable is not "supported" - see <see cref="AlgorandChainRegistry"/>. Exposed publicly via
    /// <c>GET /chains</c> (<see cref="Controllers.ChainsController"/>) for relying parties to discover.
    /// </summary>
    public interface IAlgorandChainRegistry
    {
        Task<IReadOnlyList<AlgorandChain>> GetSupportedChainsAsync(CancellationToken cancellationToken = default);

        /// <summary>Looks up a chain by its Algorand genesis id (e.g. <c>"mainnet-v1.0"</c>). Returns <c>null</c> if not currently supported.</summary>
        Task<AlgorandChain?> TryGetChainAsync(string genesisId, CancellationToken cancellationToken = default);

        /// <summary>Looks up a chain by Aramid's numeric chain id (e.g. <c>416101</c> for Voi). Returns <c>null</c> if not currently supported or Aramid doesn't bridge to it.</summary>
        Task<AlgorandChain?> TryGetChainByAramidIdAsync(long aramidChainId, CancellationToken cancellationToken = default);
    }
}
