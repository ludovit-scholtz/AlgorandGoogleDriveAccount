namespace BiatecOIDC.BusinessLogic
{
    /// <summary>
    /// Thrown by <see cref="IAssetValuationService"/> when an asset's USD value can't be determined (no
    /// Biatec Router route found, or the router is unreachable). Every asset spent in a signing request is
    /// subject to the spending limit, so this fails the whole <c>POST /wallet/sign</c> request closed
    /// (mapped to 503 by <c>WalletController</c>) rather than silently treating the unpriceable asset as
    /// zero-value spend.
    /// </summary>
    public sealed class AssetValuationException : Exception
    {
        /// <summary>The asset id (0 = ALGO) that could not be priced.</summary>
        public ulong AssetId { get; }

        public AssetValuationException(ulong assetId, Exception innerException)
            : base($"Unable to determine the USD value of asset {assetId} via the Biatec Router.", innerException)
        {
            AssetId = assetId;
        }
    }
}
