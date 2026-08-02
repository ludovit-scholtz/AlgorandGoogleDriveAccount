using Microsoft.Extensions.Caching.Memory;

namespace BiatecOIDC.BusinessLogic
{
    /// <inheritdoc cref="IAlgorandChainRegistry"/>
    public sealed class AlgorandChainRegistry : IAlgorandChainRegistry
    {
        private const string CacheKey = "AlgorandChainRegistry:SupportedChains";

        // This is only ever liveness-check/discovery data, not security-sensitive and cheap to rediscover
        // per pod, so a per-pod in-memory cache is the right fit rather than adding it to Redis alongside
        // BiatecOIDC's actual session/token state.
        private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(10);

        private readonly IPublicAlgodDataSource _dataSource;
        private readonly IMemoryCache _cache;

        public AlgorandChainRegistry(IPublicAlgodDataSource dataSource, IMemoryCache cache)
        {
            _dataSource = dataSource;
            _cache = cache;
        }

        public async Task<IReadOnlyList<AlgorandChain>> GetSupportedChainsAsync(CancellationToken cancellationToken = default)
        {
            if (_cache.TryGetValue(CacheKey, out IReadOnlyList<AlgorandChain>? cached) && cached != null)
            {
                return cached;
            }

            var chains = await DiscoverSupportedChainsAsync(cancellationToken);
            _cache.Set(CacheKey, chains, CacheDuration);
            return chains;
        }

        public async Task<AlgorandChain?> TryGetChainAsync(string genesisId, CancellationToken cancellationToken = default)
        {
            var chains = await GetSupportedChainsAsync(cancellationToken);
            return chains.FirstOrDefault(c => string.Equals(c.GenesisId, genesisId, StringComparison.Ordinal));
        }

        public async Task<AlgorandChain?> TryGetChainByAramidIdAsync(long aramidChainId, CancellationToken cancellationToken = default)
        {
            var chains = await GetSupportedChainsAsync(cancellationToken);
            return chains.FirstOrDefault(c => c.AramidChainId == aramidChainId);
        }

        private async Task<IReadOnlyList<AlgorandChain>> DiscoverSupportedChainsAsync(CancellationToken cancellationToken)
        {
            var genesisList = await _dataSource.GetGenesisListAsync(cancellationToken);
            var chains = new List<AlgorandChain>();

            foreach (var entry in genesisList)
            {
                if (string.IsNullOrWhiteSpace(entry.GenesisHash))
                {
                    continue; // placeholder entry (e.g. a local sandbox) - not a real chain to expose
                }

                IReadOnlyList<PublicAlgodProvider> providers;
                try
                {
                    providers = await _dataSource.GetProvidersAsync(entry.Network, cancellationToken);
                }
                catch (Exception)
                {
                    continue; // this chain's provider list is unreachable - not fatal to the others
                }

                var liveProvider = await FindFirstLiveMatchingProviderAsync(entry, providers, cancellationToken);
                if (liveProvider == null)
                {
                    continue; // listed, but no currently-live provider reports the right genesis hash
                }

                chains.Add(new AlgorandChain
                {
                    GenesisId = entry.Network,
                    GenesisHash = entry.GenesisHash,
                    Name = entry.Name,
                    AramidChainId = long.TryParse(entry.ChainId, out var aramidChainId) ? aramidChainId : null,
                    AlgodApiAddress = liveProvider.AlgodHost,
                    AlgodApiToken = liveProvider.Token,
                    AlgodApiTokenHeader = liveProvider.Header
                });
            }

            return chains;
        }

        private async Task<PublicAlgodProvider?> FindFirstLiveMatchingProviderAsync(GenesisListEntry entry, IReadOnlyList<PublicAlgodProvider> providers, CancellationToken cancellationToken)
        {
            foreach (var provider in providers)
            {
                string? liveHash;
                try
                {
                    liveHash = await _dataSource.TryGetLiveGenesisHashAsync(provider, cancellationToken);
                }
                catch (Exception)
                {
                    liveHash = null;
                }

                if (liveHash != null && string.Equals(liveHash, entry.GenesisHash, StringComparison.Ordinal))
                {
                    return provider;
                }
            }

            return null;
        }
    }
}
