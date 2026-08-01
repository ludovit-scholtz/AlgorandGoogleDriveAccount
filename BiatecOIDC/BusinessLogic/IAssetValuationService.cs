namespace BiatecOIDC.BusinessLogic
{
    /// <summary>
    /// Prices a spent Algorand asset (ALGO or any ASA) in USD, for daily/weekly/monthly spending-limit
    /// enforcement (see <see cref="ISpendingLimitService"/>). Every payment/asset-transfer in a signing
    /// request is subject to the limit, so an asset that can't be priced fails the request rather than
    /// being silently treated as free.
    /// </summary>
    public interface IAssetValuationService
    {
        /// <summary>
        /// The USD value of <paramref name="amountBaseUnits"/> base units of <paramref name="assetId"/>
        /// (<c>0</c> = native ALGO), right now. <c>0</c> base units always values at exactly 0 USD without
        /// consulting the router.
        /// </summary>
        /// <exception cref="AssetValuationException">The asset couldn't be priced (no route, or the router is unreachable).</exception>
        Task<decimal> GetUsdValueAsync(ulong assetId, ulong amountBaseUnits, CancellationToken cancellationToken = default);
    }
}
