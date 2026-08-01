namespace BiatecOIDC.Model
{
    /// <summary>
    /// Bound from the <c>SpendingLimits</c> configuration section. Controls how a spent asset (ALGO or
    /// any ASA) is converted into a USD value for daily/weekly/monthly spending-limit enforcement - see
    /// <c>BusinessLogic.IAssetValuationService</c>.
    /// </summary>
    public class SpendingLimitsConfiguration
    {
        /// <summary>
        /// The asset every spent asset is quoted against via the Biatec Router to derive its USD value
        /// (1 unit of this asset is treated as ~1 USD). Defaults to mainnet USDC (31566704). Point this at
        /// a testnet USDC/USD-pegged asset id when the router is configured against TestNet.
        /// </summary>
        public ulong UsdReferenceAssetId { get; set; } = 31566704;

        /// <summary>Decimal places of <see cref="UsdReferenceAssetId"/> (6 for USDC on both MainNet and TestNet).</summary>
        public int UsdReferenceAssetDecimals { get; set; } = 6;
    }
}
