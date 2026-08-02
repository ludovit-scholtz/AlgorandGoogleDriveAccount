namespace BiatecMCP.BusinessLogic
{
    /// <summary>
    /// Raw access to the public Algorand chain/provider registry
    /// (https://scholtz.github.io/AlgorandPublicData) - separated from <see cref="IAlgorandChainRegistry"/>'s
    /// selection/caching logic purely so that logic can be unit-tested with canned data instead of live HTTP
    /// calls. <see cref="PublicAlgodDataSource"/> is the real implementation; nothing else should implement
    /// this outside tests.
    /// </summary>
    public interface IPublicAlgodDataSource
    {
        Task<IReadOnlyList<GenesisListEntry>> GetGenesisListAsync(CancellationToken cancellationToken = default);

        /// <summary>Returns an empty list (never throws for a "not found"/empty case) if <paramref name="genesisId"/> has no published provider list.</summary>
        Task<IReadOnlyList<PublicAlgodProvider>> GetProvidersAsync(string genesisId, CancellationToken cancellationToken = default);

        /// <summary>
        /// Calls <paramref name="provider"/>'s own algod node and returns the base64 genesis hash it
        /// reports, or <c>null</c> if the node is unreachable/errors - never throws.
        /// </summary>
        Task<string?> TryGetLiveGenesisHashAsync(PublicAlgodProvider provider, CancellationToken cancellationToken = default);
    }
}
