using BiatecMCP.BusinessLogic.Scan;

namespace BiatecMCP.Helper
{
    /// <summary>
    /// A pool's APR as Biatec Scan's own web UI computes it (<c>poolFees.ts</c>): the fees liquidity providers earned over a trailing
    /// window relative to the pool's TVL, annualised. A trailing-fee estimate only - it ignores impermanent loss and any incentives, and a
    /// tiny TVL inflates it.
    /// </summary>
    public static class PoolAprCalculator
    {
        private const double DaysPerYear = 365;

        /// <summary>Fraction of trade volume liquidity providers earn: <c>lpFee * (1 - protocolFeePortion)</c>.</summary>
        public static double? LpFeeRate(ScanPool pool) =>
            pool.LpFee == null ? null : pool.LpFee.Value * (1 - (pool.ProtocolFeePortion ?? 0));

        /// <summary>Total value locked in the pool (both sides), or <c>null</c> when neither side is priced.</summary>
        public static double? TvlUsd(ScanPool pool) =>
            pool.TotalTvlAssetAInUsd == null && pool.TotalTvlAssetBInUsd == null
                ? null
                : (pool.TotalTvlAssetAInUsd ?? 0) + (pool.TotalTvlAssetBInUsd ?? 0);

        public static double? Apr24h(ScanPool pool) => Apr(pool, pool.Volume24H, 1);

        public static double? Apr7d(ScanPool pool) => Apr(pool, pool.Volume7D, 7);

        private static double? Apr(ScanPool pool, double? volume, int windowDays)
        {
            var rate = LpFeeRate(pool);
            var tvl = TvlUsd(pool);
            if (rate == null || volume == null || tvl == null || tvl <= 0)
            {
                return null;
            }

            return rate.Value * volume.Value / tvl.Value * (DaysPerYear / windowDays);
        }
    }
}
