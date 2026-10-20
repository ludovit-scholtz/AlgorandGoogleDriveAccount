using System.ComponentModel;
using System.Globalization;
using BiatecMCP.BusinessLogic;
using BiatecMCP.BusinessLogic.Scan;
using BiatecMCP.Helper;
using ModelContextProtocol.Server;

namespace BiatecMCP.MCP
{
    /// <summary>
    /// Read-only tools over Biatec Scan (the Biatec DEX explorer, <c>scan.biatec.io</c>): find assets, and find the liquidity pools that pay
    /// liquidity providers the most (APR) - e.g. "which EUR/USDC pools have the highest APR?". Nothing here touches a wallet, signs or moves
    /// funds; BiatecMCP reads Scan with its own anonymous ARC-14 access identity (see <see cref="Arc14ScanAuthTokenProvider"/>).
    /// </summary>
    [McpServerToolType]
    public class BiatecScanMCP
    {
        /// <summary>How many candidate assets one symbol may expand to (e.g. several tokens called "EUR"); the highest-TVL ones win.</summary>
        private const int MaxCandidatesPerSymbol = 3;

        private const int PoolsPerAsset = 500;
        private const int AssetSearchSize = 100;

        private static readonly string[] PoolSortKeys = ["apr7d", "apr24h", "tvl", "volume24h", "volume7d"];
        private static readonly string[] AssetSortKeys = ["apr7d", "apr24h"];

        private readonly INetworkResolver _networkResolver;
        private readonly IBiatecScanClient _scan;
        private readonly ILogger<BiatecScanMCP> _logger;

        public BiatecScanMCP(INetworkResolver networkResolver, IBiatecScanClient scan, ILogger<BiatecScanMCP> logger)
        {
            _networkResolver = networkResolver;
            _scan = scan;
            _logger = logger;
        }

        public class ScanAssetSummary
        {
            public ulong AssetId { get; set; }
            public string Symbol { get; set; } = string.Empty;
            public string Name { get; set; } = string.Empty;
            public double PriceUsd { get; set; }
            public double TvlUsd { get; set; }
            public int PoolsCount { get; set; }
        }

        public class SearchBiatecScanAssetsResponse
        {
            public string Network { get; set; } = string.Empty;
            public List<ScanAssetSummary> Assets { get; set; } = new();
            public string Error { get; set; } = string.Empty;
            public string ErrorType { get; set; } = string.Empty;
        }

        public class ScanPoolSummary
        {
            public ulong PoolAppId { get; set; }
            public string PoolAddress { get; set; } = string.Empty;
            public string Protocol { get; set; } = string.Empty;
            public string AmmType { get; set; } = string.Empty;
            public ulong AssetIdA { get; set; }
            public string SymbolA { get; set; } = string.Empty;
            public ulong AssetIdB { get; set; }
            public string SymbolB { get; set; } = string.Empty;
            /// <summary>Total value locked in USD, or <c>null</c> when Scan has no price for either side.</summary>
            public double? TvlUsd { get; set; }

            /// <summary><c>null</c> means Scan has no volume figure (not "no trading").</summary>
            public double? Volume24hUsd { get; set; }

            public double? Volume7dUsd { get; set; }

            /// <summary>The swap fee liquidity providers earn, in percent of volume (the protocol's cut already deducted).</summary>
            public double? LpFeePercent { get; set; }

            /// <summary>Annualised LP fee yield from the trailing 24h / 7 days, in percent; <c>null</c> when it can't be computed (no TVL or fee data).</summary>
            public double? Apr24hPercent { get; set; }

            public double? Apr7dPercent { get; set; }

            /// <summary>0-100 risk score from Biatec Scan; above 80 is a known scam (hidden unless includeScam is set), anything above 0 deserves caution.</summary>
            public int ScamRating { get; set; }
        }

        public class ResolvedScanAsset
        {
            public string Input { get; set; } = string.Empty;
            public List<ScanAssetSummary> Matches { get; set; } = new();
        }

        public class FindBestLiquidityPoolsResponse
        {
            public string Network { get; set; } = string.Empty;
            public string SortedBy { get; set; } = string.Empty;

            /// <summary>How each asset input was understood - a symbol can match several tokens, so check these are the ones you meant.</summary>
            public List<ResolvedScanAsset> ResolvedAssets { get; set; } = new();

            public int TotalMatchingPools { get; set; }

            /// <summary>True when Scan hit its per-request pool cap, so the list may be missing pools.</summary>
            public bool Truncated { get; set; }
            public List<ScanPoolSummary> Pools { get; set; } = new();
            public string Note { get; set; } = string.Empty;
            public string Error { get; set; } = string.Empty;
            public string ErrorType { get; set; } = string.Empty;
        }

        public class ScanAssetApr
        {
            public ulong AssetId { get; set; }
            public string Symbol { get; set; } = string.Empty;
            public string Name { get; set; } = string.Empty;
            public double TvlUsd { get; set; }
            public double Volume24hUsd { get; set; }
            public int PoolCount { get; set; }
            public double Apr24hPercent { get; set; }
            public double Apr7dPercent { get; set; }
        }

        public class GetBiatecScanTopAssetsByAprResponse
        {
            public string Network { get; set; } = string.Empty;
            public List<ScanAssetApr> Assets { get; set; } = new();
            public string Note { get; set; } = string.Empty;
            public string Error { get; set; } = string.Empty;
            public string ErrorType { get; set; } = string.Empty;
        }

        [McpServerTool(Name = "searchBiatecScanAssets"),
         Description("Looks up assets known to Biatec Scan (the Biatec DEX explorer) by symbol, name or asset id, returning the asset id, symbol, USD price, TVL and number of pools, highest TVL first. Read-only. Use it to find the right asset id when a symbol like 'EUR' matches several tokens, then pass the id to findBestLiquidityPools.")]
        public async Task<SearchBiatecScanAssetsResponse> SearchBiatecScanAssets(
            [Description("Symbol, name or numeric asset id to look for, e.g. 'USDC', 'euro', '31566704'.")] string query,
            [Description("The network - an exact code from listSupportedNetworks that Biatec Scan covers (algorand-mainnet, algorand-testnet, voi-mainnet).")] string network = "algorand-mainnet",
            [Description("Maximum number of assets to return (1-50).")] int limit = 10)
        {
            try
            {
                var (code, chain) = await ResolveScanNetworkAsync(network);
                var assets = await FindAssetsAsync(code, chain, query);
                return new SearchBiatecScanAssetsResponse
                {
                    Network = code,
                    Assets = assets.Take(Math.Clamp(limit, 1, 50)).Select(ToSummary).ToList()
                };
            }
            catch (Exception ex)
            {
                var (message, type) = Describe(ex, nameof(SearchBiatecScanAssets));
                return new SearchBiatecScanAssetsResponse { Network = network, Error = message, ErrorType = type };
            }
        }

        [McpServerTool(Name = "findBestLiquidityPools"),
         Description("Finds the liquidity pools with the highest APR on Biatec Scan (the Biatec DEX explorer), across the Biatec, Pact and Tinyman AMMs. Give one asset to see every pool it is in, or two assets for pools between them - e.g. assetA 'EUR', assetB 'USDC' for the best EUR/USDC pools. Assets are symbols or asset ids; a symbol may match several tokens, so check ResolvedAssets (use searchBiatecScanAssets to pick exact ids). APR is the trailing 24h/7-day liquidity-provider fee yield annualised - an estimate that ignores impermanent loss and incentives, and tiny pools show inflated values (hence the minTvlUsd filter). Read-only: nothing is deposited or signed.")]
        public async Task<FindBestLiquidityPoolsResponse> FindBestLiquidityPools(
            [Description("First asset: a symbol ('EUR', 'USDC', 'ALGO') or an asset id.")] string assetA,
            [Description("Optional second asset (symbol or id) - only pools between the two assets are returned. Omit to list every pool of assetA.")] string? assetB = null,
            [Description("The network - an exact code from listSupportedNetworks that Biatec Scan covers (algorand-mainnet, algorand-testnet, voi-mainnet).")] string network = "algorand-mainnet",
            [Description("How to rank: apr7d (default, smoother), apr24h, tvl, volume24h or volume7d.")] string sortBy = "apr7d",
            [Description("Hide pools with less total value locked than this (USD). Default 1000 - tiny pools often show absurd APRs.")] double minTvlUsd = 1000,
            [Description("Optional AMM filter: Biatec, Pact or Tiny.")] string? protocol = null,
            [Description("Maximum number of pools to return (1-50).")] int limit = 10,
            [Description("Include pools Biatec Scan rates as known scams (scam rating above 80). Default false.")] bool includeScam = false)
        {
            try
            {
                var sortKey = (sortBy ?? string.Empty).Trim().ToLowerInvariant();
                if (!PoolSortKeys.Contains(sortKey))
                {
                    return new FindBestLiquidityPoolsResponse { Network = network, Error = $"sortBy must be one of: {string.Join(", ", PoolSortKeys)}.", ErrorType = "InvalidRequest" };
                }

                var protocolName = NormalizeProtocol(protocol);
                if (!string.IsNullOrWhiteSpace(protocol) && protocolName == null)
                {
                    return new FindBestLiquidityPoolsResponse { Network = network, Error = "protocol must be Biatec, Pact or Tiny.", ErrorType = "InvalidRequest" };
                }

                if (string.IsNullOrWhiteSpace(assetA))
                {
                    return new FindBestLiquidityPoolsResponse { Network = network, Error = "assetA is required.", ErrorType = "InvalidRequest" };
                }

                var (code, chain) = await ResolveScanNetworkAsync(network);
                var response = new FindBestLiquidityPoolsResponse { Network = code, SortedBy = sortKey };

                var aTask = ResolveSymbolAsync(code, chain, assetA);
                var bTask = string.IsNullOrWhiteSpace(assetB) ? Task.FromResult<List<ScanAsset>?>(null) : ResolveSymbolAsync(code, chain, assetB)!;
                await Task.WhenAll(aTask, bTask); // both awaited, so a failure of one never leaves the other unobserved
                var aMatches = aTask.Result;
                var bMatches = bTask.Result;
                response.ResolvedAssets.Add(new ResolvedScanAsset { Input = assetA, Matches = aMatches.Select(ToSummary).ToList() });
                if (bMatches != null)
                {
                    response.ResolvedAssets.Add(new ResolvedScanAsset { Input = assetB!, Matches = bMatches.Select(ToSummary).ToList() });
                }

                if (aMatches.Count == 0 || (bMatches != null && bMatches.Count == 0))
                {
                    response.Error = "No asset matched " + string.Join(" and ", response.ResolvedAssets.Where(r => r.Matches.Count == 0).Select(r => $"'{r.Input}'")) + ". Try searchBiatecScanAssets.";
                    response.ErrorType = "AssetNotFound";
                    return response;
                }

                var aIds = aMatches.Select(a => a.Index).ToHashSet();
                var bIds = bMatches?.Select(b => b.Index).ToHashSet();

                // Pools are looked up per candidate asset A (an asset alone matches either side of a pool); the pair is filtered here.
                var pools = new Dictionary<string, ScanPool>();
                var truncated = false;
                var perAsset = await Task.WhenAll(aMatches.Select(a => _scan.GetPoolsForAssetAsync(code, chain, a.Index, protocolName, PoolsPerAsset)));
                var fetched = perAsset.SelectMany(list => list).ToList();

                // Scan returns at most PoolsPerAsset rows per request. When an asset hit that cap the pair filter below could miss pools, so for a
                // pair the exact (A, B) queries - both pool orders - are made instead; for a single asset the answer is flagged as partial.
                if (perAsset.Any(list => list.Count >= PoolsPerAsset))
                {
                    if (bMatches != null)
                    {
                        var pairs = aMatches.SelectMany(a => bMatches.Where(b => b.Index != a.Index).SelectMany(b => new[] { (a.Index, b.Index), (b.Index, a.Index) })).Distinct().ToList();
                        var perPair = await Task.WhenAll(pairs.Select(pair => _scan.GetPoolsForPairAsync(code, chain, pair.Item1, pair.Item2, protocolName, PoolsPerAsset)));
                        fetched = perPair.SelectMany(list => list).ToList();
                        truncated = perPair.Any(list => list.Count >= PoolsPerAsset);
                    }
                    else
                    {
                        truncated = true;
                    }
                }

                foreach (var pool in fetched)
                {
                    if (pool.AssetIdA == null || pool.AssetIdB == null)
                    {
                        continue;
                    }

                    var matchesPair = bIds == null
                        || (aIds.Contains(pool.AssetIdA.Value) && bIds.Contains(pool.AssetIdB.Value))
                        || (aIds.Contains(pool.AssetIdB.Value) && bIds.Contains(pool.AssetIdA.Value));
                    if (matchesPair)
                    {
                        pools[$"{pool.PoolAddress}:{pool.PoolAppId}:{pool.AssetIdA}:{pool.AssetIdB}"] = pool;
                    }
                }

                var eligible = pools.Values
                    .Where(p => includeScam || p.ScamRating <= 80)
                    .Where(p => (PoolAprCalculator.TvlUsd(p) ?? 0) >= minTvlUsd)
                    .ToList();
                var ranked = eligible.OrderByDescending(p => SortValue(p, sortKey)).ThenByDescending(p => PoolAprCalculator.TvlUsd(p) ?? 0).ToList();
                var top = ranked.Take(Math.Clamp(limit, 1, 50)).ToList();

                response.TotalMatchingPools = ranked.Count;
                response.Pools = await ToPoolSummariesAsync(code, chain, top, aMatches.Concat(bMatches ?? new List<ScanAsset>()));
                response.Truncated = truncated;
                response.Note = ranked.Count == 0
                    ? $"No pool matched with at least ${minTvlUsd.ToString("0.##", CultureInfo.InvariantCulture)} TVL; lower minTvlUsd to see smaller ones."
                    : "APR = trailing liquidity-provider fees / TVL, annualised. It is not guaranteed future yield and excludes impermanent loss; check ScamRating and TVL before depositing anywhere. A pool holding a token Scan cannot price can show a distorted TVL and APR."
                    + (truncated ? " Scan returned its maximum number of pools for this asset, so the list may be incomplete - name a second asset or filter by protocol to narrow it." : string.Empty);
                return response;
            }
            catch (Exception ex)
            {
                var (message, type) = Describe(ex, nameof(FindBestLiquidityPools));
                return new FindBestLiquidityPoolsResponse { Network = network, Error = message, ErrorType = type };
            }
        }

        [McpServerTool(Name = "getBiatecScanTopAssetsByApr"),
         Description("Ranks assets by the liquidity-provider APR Biatec Scan reports for them (their pools combined), highest first - a quick way to see where fee yield is best across the whole DEX. For the best pools of specific assets (e.g. EUR/USDC) use findBestLiquidityPools instead. APR is an annualised trailing-fee estimate, not guaranteed yield. Read-only.")]
        public async Task<GetBiatecScanTopAssetsByAprResponse> GetBiatecScanTopAssetsByApr(
            [Description("The network - an exact code from listSupportedNetworks that Biatec Scan covers (algorand-mainnet, algorand-testnet, voi-mainnet).")] string network = "algorand-mainnet",
            [Description("Rank by apr7d (default, smoother) or apr24h.")] string sortBy = "apr7d",
            [Description("Hide assets with less total value locked than this (USD). Default 10000 - tiny assets often show absurd APRs.")] double minTvlUsd = 10000,
            [Description("Optional AMM filter: Biatec, Pact or Tiny. Omit for all AMMs combined.")] string? protocol = null,
            [Description("Maximum number of assets to return (1-50).")] int limit = 10)
        {
            try
            {
                var sortKey = (sortBy ?? string.Empty).Trim().ToLowerInvariant();
                if (!AssetSortKeys.Contains(sortKey))
                {
                    return new GetBiatecScanTopAssetsByAprResponse { Network = network, Error = $"sortBy must be one of: {string.Join(", ", AssetSortKeys)}.", ErrorType = "InvalidRequest" };
                }

                var protocolName = NormalizeProtocol(protocol);
                if (!string.IsNullOrWhiteSpace(protocol) && protocolName == null)
                {
                    return new GetBiatecScanTopAssetsByAprResponse { Network = network, Error = "protocol must be Biatec, Pact or Tiny.", ErrorType = "InvalidRequest" };
                }

                var (code, chain) = await ResolveScanNetworkAsync(network);
                var stats = await _scan.GetAssetStatsAsync(code, chain, protocolName, sortKey == "apr24h" ? "Apr24h" : "Apr7d");

                // With no protocol filter the API returns the combined row plus one per protocol; keep one row per asset.
                var rows = stats.Where(s => protocolName == null ? s.Protocol == null : string.Equals(s.Protocol, protocolName, StringComparison.OrdinalIgnoreCase))
                    .Where(s => (s.TvlUsd ?? 0) >= minTvlUsd)
                    .OrderByDescending(s => (sortKey == "apr24h" ? s.Apr24h : s.Apr7d) ?? double.NegativeInfinity)
                    .Take(Math.Clamp(limit, 1, 50))
                    .Select(s => new ScanAssetApr
                    {
                        AssetId = s.AssetId,
                        Symbol = s.UnitName ?? string.Empty,
                        Name = s.AssetName ?? string.Empty,
                        TvlUsd = s.TvlUsd ?? 0,
                        Volume24hUsd = s.Volume24hUsd ?? 0,
                        PoolCount = s.PoolCount ?? 0,
                        Apr24hPercent = s.Apr24h ?? 0,
                        Apr7dPercent = s.Apr7d ?? 0
                    })
                    .ToList();

                return new GetBiatecScanTopAssetsByAprResponse
                {
                    Network = code,
                    Assets = rows,
                    Note = "APR = trailing liquidity-provider fees / TVL, annualised; not guaranteed future yield. This ranking has NO scam screening - check the pools with findBestLiquidityPools (it shows ScamRating) before depositing anywhere."
                };
            }
            catch (Exception ex)
            {
                var (message, type) = Describe(ex, nameof(GetBiatecScanTopAssetsByApr));
                return new GetBiatecScanTopAssetsByAprResponse { Network = network, Error = message, ErrorType = type };
            }
        }

        // ───────────────────────── helpers ─────────────────────────

        private async Task<(string Code, AlgorandChain Chain)> ResolveScanNetworkAsync(string network)
        {
            var resolved = await _networkResolver.ResolveAsync(network);
            if (resolved is not { Family: ChainFamily.Avm, AvmChain: not null } || !_scan.IsConfigured(resolved.Code))
            {
                throw new ScanApiException($"Biatec Scan covers these networks only: {string.Join(", ", _scan.ConfiguredNetworks)} (exact codes from listSupportedNetworks); '{network}' is not one of them.");
            }

            return (resolved.Code, resolved.AvmChain);
        }

        /// <summary>Assets matching free text, or the one asset when it is a numeric id; highest TVL first, destroyed assets dropped.</summary>
        private async Task<List<ScanAsset>> FindAssetsAsync(string code, AlgorandChain chain, string query)
        {
            var text = (query ?? string.Empty).Trim();
            if (text.Length == 0)
            {
                throw new ArgumentException("A search text or asset id is required.");
            }

            var found = ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var id)
                ? await _scan.GetAssetsByIdAsync(code, chain, new[] { id })
                : await _scan.SearchAssetsAsync(code, chain, text, AssetSearchSize);
            return found.Where(a => !a.Deleted).OrderByDescending(a => a.TvlUsd ?? 0).ToList();
        }

        /// <summary>
        /// Turns an asset input into candidate assets: a numeric id is exact; a symbol prefers exact symbol matches (ranked by TVL, at most a few)
        /// and falls back to partial name matches only when none exists.
        /// </summary>
        private async Task<List<ScanAsset>> ResolveSymbolAsync(string code, AlgorandChain chain, string input)
        {
            var text = input.Trim();
            var found = await FindAssetsAsync(code, chain, text);
            if (ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out _))
            {
                return found.Take(1).ToList();
            }

            var exact = found.Where(a => string.Equals(a.Params?.UnitName, text, StringComparison.OrdinalIgnoreCase)).ToList();
            var pool = exact.Count > 0 ? exact : found;
            return pool.Where(a => (a.TvlUsd ?? 0) > 0 || (a.PoolsCount ?? 0) > 0).Take(MaxCandidatesPerSymbol).ToList();
        }

        private async Task<List<ScanPoolSummary>> ToPoolSummariesAsync(string code, AlgorandChain chain, List<ScanPool> pools, IEnumerable<ScanAsset> known)
        {
            var symbols = known.GroupBy(a => a.Index).ToDictionary(g => g.Key, g => g.First().Params?.UnitName ?? string.Empty);
            var unknown = pools.SelectMany(p => new[] { p.AssetIdA!.Value, p.AssetIdB!.Value }).Where(i => !symbols.ContainsKey(i)).Distinct().ToList();
            if (unknown.Count > 0)
            {
                foreach (var asset in await _scan.GetAssetsByIdAsync(code, chain, unknown))
                {
                    symbols[asset.Index] = asset.Params?.UnitName ?? string.Empty;
                }
            }

            string Symbol(ulong id) => symbols.TryGetValue(id, out var s) && s.Length > 0 ? s : (id == 0 ? "native" : string.Empty);

            return pools.Select(p => new ScanPoolSummary
            {
                PoolAppId = p.PoolAppId,
                PoolAddress = p.PoolAddress ?? string.Empty,
                Protocol = p.Protocol ?? string.Empty,
                AmmType = p.AmmType ?? string.Empty,
                AssetIdA = p.AssetIdA!.Value,
                SymbolA = Symbol(p.AssetIdA.Value),
                AssetIdB = p.AssetIdB!.Value,
                SymbolB = Symbol(p.AssetIdB.Value),
                TvlUsd = PoolAprCalculator.TvlUsd(p),
                Volume24hUsd = p.Volume24H,
                Volume7dUsd = p.Volume7D,
                LpFeePercent = PoolAprCalculator.LpFeeRate(p) * 100,
                Apr24hPercent = PoolAprCalculator.Apr24h(p) * 100,
                Apr7dPercent = PoolAprCalculator.Apr7d(p) * 100,
                ScamRating = p.ScamRating
            }).ToList();
        }

        private static double SortValue(ScanPool pool, string sortKey) => sortKey switch
        {
            "apr24h" => PoolAprCalculator.Apr24h(pool) ?? double.NegativeInfinity,
            "tvl" => PoolAprCalculator.TvlUsd(pool) ?? double.NegativeInfinity,
            "volume24h" => pool.Volume24H ?? double.NegativeInfinity,
            "volume7d" => pool.Volume7D ?? double.NegativeInfinity,
            _ => PoolAprCalculator.Apr7d(pool) ?? double.NegativeInfinity
        };

        /// <summary>The canonical protocol name, or <c>null</c> when none was given or it isn't one of the three real AMMs.</summary>
        private static string? NormalizeProtocol(string? protocol) => protocol?.Trim().ToLowerInvariant() switch
        {
            "biatec" => "Biatec",
            "pact" => "Pact",
            "tiny" or "tinyman" => "Tiny",
            _ => null
        };

        private static ScanAssetSummary ToSummary(ScanAsset asset) => new()
        {
            AssetId = asset.Index,
            Symbol = asset.Params?.UnitName ?? string.Empty,
            Name = asset.Params?.Name ?? string.Empty,
            PriceUsd = asset.PriceUSD ?? 0,
            TvlUsd = asset.TvlUsd ?? 0,
            PoolsCount = asset.PoolsCount ?? 0
        };

        private (string Message, string ErrorType) Describe(Exception ex, string toolName)
        {
            switch (ex)
            {
                case OperationCanceledException:
                    return ("The request was cancelled.", "Cancelled");
                case ScanApiException e:
                    return (e.Message, "ScanUnavailable");
                case ArgumentException e:
                    return (e.Message, "InvalidRequest");
                default:
                    _logger.LogError(ex, "Unexpected error in MCP tool {ToolName}.", toolName);
                    return ("An unexpected error occurred while processing the request. It has been logged server-side.", "UnexpectedError");
            }
        }
    }
}
