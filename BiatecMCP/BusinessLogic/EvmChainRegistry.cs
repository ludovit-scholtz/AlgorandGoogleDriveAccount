using Microsoft.Extensions.Caching.Memory;

namespace BiatecMCP.BusinessLogic
{
    /// <inheritdoc cref="IEvmChainRegistry"/>
    public sealed class EvmChainRegistry : IEvmChainRegistry
    {
        private const string ChainListCacheKey = "EvmChainRegistry:ChainList";
        private const string ChainCacheKeyPrefix = "EvmChainRegistry:Chain:";

        // The raw chains.json list changes rarely - 10 minutes matches AlgorandChainRegistry's cache
        // duration. A resolved *live* chain is cached for less time (RPC availability can change faster
        // than the published list), and a negative result (chain unknown or currently unreachable) even
        // less, so a transient outage or a typo'd name doesn't stick around too long.
        private static readonly TimeSpan ChainListCacheDuration = TimeSpan.FromMinutes(10);
        private static readonly TimeSpan PositiveChainCacheDuration = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan NegativeChainCacheDuration = TimeSpan.FromMinutes(1);

        private static readonly string[] NameSuffixesToStrip = [" Mainnet", " One"];

        private readonly IPublicEvmRpcDataSource _dataSource;
        private readonly IMemoryCache _cache;

        public EvmChainRegistry(IPublicEvmRpcDataSource dataSource, IMemoryCache cache)
        {
            _dataSource = dataSource;
            _cache = cache;
        }

        public async Task<EvmChain?> TryGetChainAsync(long chainId, CancellationToken cancellationToken = default)
        {
            var cacheKey = ChainCacheKeyPrefix + chainId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (_cache.TryGetValue(cacheKey, out EvmChain? cached))
            {
                return cached;
            }

            var chainList = await GetChainListAsync(cancellationToken);
            var entry = chainList.FirstOrDefault(c => c.ChainId == chainId);
            if (entry == null)
            {
                _cache.Set(cacheKey, (EvmChain?)null, NegativeChainCacheDuration);
                return null;
            }

            var resolved = await ResolveLiveChainAsync(entry, cancellationToken);
            _cache.Set(cacheKey, resolved, resolved != null ? PositiveChainCacheDuration : NegativeChainCacheDuration);
            return resolved;
        }

        public async Task<EvmChain?> TryGetChainByNameAsync(string name, CancellationToken cancellationToken = default)
        {
            var chainList = await GetChainListAsync(cancellationToken);

            var entry = chainList.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase))
                ?? chainList.FirstOrDefault(c => string.Equals(c.ShortName, name, StringComparison.OrdinalIgnoreCase))
                ?? chainList.FirstOrDefault(c => string.Equals(NormalizeName(c.Name), NormalizeName(name), StringComparison.OrdinalIgnoreCase));

            return entry == null ? null : await TryGetChainAsync(entry.ChainId, cancellationToken);
        }

        private async Task<IReadOnlyList<EvmChainListEntry>> GetChainListAsync(CancellationToken cancellationToken)
        {
            if (_cache.TryGetValue(ChainListCacheKey, out IReadOnlyList<EvmChainListEntry>? cached) && cached != null)
            {
                return cached;
            }

            var chainList = await _dataSource.GetChainListAsync(cancellationToken);
            _cache.Set(ChainListCacheKey, chainList, ChainListCacheDuration);
            return chainList;
        }

        private async Task<EvmChain?> ResolveLiveChainAsync(EvmChainListEntry entry, CancellationToken cancellationToken)
        {
            foreach (var rpcUrl in entry.RpcCandidates)
            {
                long? liveChainId;
                try
                {
                    liveChainId = await _dataSource.TryGetLiveChainIdAsync(rpcUrl, cancellationToken);
                }
                catch (Exception)
                {
                    liveChainId = null;
                }

                if (liveChainId == entry.ChainId)
                {
                    return new EvmChain
                    {
                        ChainId = entry.ChainId,
                        Name = entry.Name,
                        RpcUrl = rpcUrl,
                        NativeCurrencySymbol = entry.NativeCurrencySymbol,
                        NativeCurrencyDecimals = entry.NativeCurrencyDecimals
                    };
                }
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
