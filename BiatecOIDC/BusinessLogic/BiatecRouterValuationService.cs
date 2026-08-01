using BiatecOIDC.Model;
using Microsoft.Extensions.Options;

namespace BiatecOIDC.BusinessLogic
{
    /// <inheritdoc cref="IAssetValuationService"/>
    /// <remarks>
    /// Prices every asset against <see cref="SpendingLimitsConfiguration.UsdReferenceAssetId"/> (mainnet
    /// USDC by default) via the Biatec Router's <c>/quote</c> endpoint, treating 1 unit of that asset as
    /// ~1 USD. Spending the reference asset itself is a 1:1 conversion computed locally, without a router
    /// round-trip, since there's nothing to quote.
    /// </remarks>
    public sealed class BiatecRouterValuationService : IAssetValuationService
    {
        private readonly IBiatecRouterQuoteClient _routerClient;
        private readonly IOptionsMonitor<SpendingLimitsConfiguration> _config;
        private readonly ILogger<BiatecRouterValuationService> _logger;

        public BiatecRouterValuationService(
            IBiatecRouterQuoteClient routerClient,
            IOptionsMonitor<SpendingLimitsConfiguration> config,
            ILogger<BiatecRouterValuationService> logger)
        {
            _routerClient = routerClient;
            _config = config;
            _logger = logger;
        }

        public async Task<decimal> GetUsdValueAsync(ulong assetId, ulong amountBaseUnits, CancellationToken cancellationToken = default)
        {
            if (amountBaseUnits == 0)
            {
                return 0m;
            }

            var usdReferenceAssetId = _config.CurrentValue.UsdReferenceAssetId;
            var usdReferenceDecimals = _config.CurrentValue.UsdReferenceAssetDecimals;

            if (assetId == usdReferenceAssetId)
            {
                return ToDecimalAmount(amountBaseUnits, usdReferenceDecimals);
            }

            long quotedUsdBaseUnits;
            try
            {
                var fromAsset = checked((long)assetId);
                var toAsset = checked((long)usdReferenceAssetId);
                var amount = checked((long)amountBaseUnits);

                quotedUsdBaseUnits = await _routerClient.QuoteAsync(fromAsset, toAsset, amount, cancellationToken);
            }
            catch (Exception ex) when (ex is not AssetValuationException)
            {
                _logger.LogWarning(ex, "Unable to price asset {AssetId} (amount {Amount}) via the Biatec Router.", assetId, amountBaseUnits);
                throw new AssetValuationException(assetId, ex);
            }

            return ToDecimalAmount((ulong)quotedUsdBaseUnits, usdReferenceDecimals);
        }

        private static decimal ToDecimalAmount(ulong baseUnits, int decimals)
        {
            return baseUnits / (decimal)Math.Pow(10, decimals);
        }
    }
}
