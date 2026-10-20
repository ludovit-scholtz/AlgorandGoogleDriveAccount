using System.Text.Json.Serialization;

namespace BiatecMCP.BusinessLogic.Scan
{
    /// <summary>The subset of Biatec Scan's <c>Pool</c> document the tools use (property names match its camelCase JSON, case-insensitively).</summary>
    public sealed class ScanPool
    {
        public string? PoolAddress { get; set; }
        public ulong PoolAppId { get; set; }
        public ulong? AssetIdA { get; set; }
        public ulong? AssetIdB { get; set; }
        public string? Protocol { get; set; }
        public string? AmmType { get; set; }

        /// <summary>Total swap fee rate as a fraction (0.003 = 0.3%).</summary>
        public double? LpFee { get; set; }

        /// <summary>Fraction of <see cref="LpFee"/> routed to the protocol instead of liquidity providers.</summary>
        public double? ProtocolFeePortion { get; set; }

        [JsonPropertyName("totalTVLAssetAInUSD")]
        public double? TotalTvlAssetAInUsd { get; set; }

        [JsonPropertyName("totalTVLAssetBInUSD")]
        public double? TotalTvlAssetBInUsd { get; set; }

        [JsonPropertyName("volume24H")]
        public double? Volume24H { get; set; }

        [JsonPropertyName("volume7D")]
        public double? Volume7D { get; set; }

        /// <summary>0-100; above 80 Biatec Scan itself treats the pool as a known scam.</summary>
        public int ScamRating { get; set; }
    }

    public sealed class ScanAssetParams
    {
        public string? Name { get; set; }
        public string? UnitName { get; set; }
        public int? Decimals { get; set; }
    }

    public sealed class ScanAsset
    {
        public ulong Index { get; set; }
        public ScanAssetParams? Params { get; set; }
        public double PriceUSD { get; set; }

        [JsonPropertyName("tvL_USD")]
        public double TvlUsd { get; set; }

        public int PoolsCount { get; set; }
        public bool Deleted { get; set; }
    }

    /// <summary>One row of Biatec Scan's per-asset stats. <see cref="Protocol"/> is <c>null</c> for the combined "all protocols" row.</summary>
    public sealed class ScanAssetStat
    {
        public ulong AssetId { get; set; }
        public string? Protocol { get; set; }
        public string? AssetName { get; set; }
        public string? UnitName { get; set; }
        public double? PriceUSD { get; set; }

        [JsonPropertyName("tvlusd")]
        public double TvlUsd { get; set; }

        [JsonPropertyName("volume24hUSD")]
        public double Volume24hUsd { get; set; }

        [JsonPropertyName("volume7dUSD")]
        public double Volume7dUsd { get; set; }

        /// <summary>Annualised LP fee yield from the trailing 24h, already in percent (12 = 12%) - unlike the per-pool figures this server computes itself.</summary>
        public double Apr24h { get; set; }

        public double Apr7d { get; set; }
        public int PoolCount { get; set; }
    }

    /// <summary>Biatec Scan answered with an error (or couldn't be reached) - the message is safe to show the connected agent.</summary>
    public sealed class ScanApiException : Exception
    {
        public ScanApiException(string message)
            : base(message)
        {
        }
    }
}
