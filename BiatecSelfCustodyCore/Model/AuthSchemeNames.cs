namespace BiatecSelfCustodyCore.Model
{
    /// <summary>Auth-related constants shared across <c>BiatecMCP</c> and <c>BiatecOIDC</c>.</summary>
    public static class AuthSchemeNames
    {
        /// <summary>
        /// Claim type added to the signed-in principal (in each provider's <c>OnTokenValidated</c>, via
        /// <c>Providers.CloudStorageProviderClaims.Stamp</c>) recording which
        /// <see cref="Providers.ICloudStorageProvider"/> the user signed in with, so downstream code
        /// knows which storage backend their self-custody account lives in.
        /// </summary>
        public const string IdpClaimType = "biatec_idp";
    }
}
