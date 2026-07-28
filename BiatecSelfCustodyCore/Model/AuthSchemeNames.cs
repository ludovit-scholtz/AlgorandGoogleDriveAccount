namespace BiatecSelfCustodyCore.Model
{
    /// <summary>
    /// Authentication scheme/claim names shared across <c>BiatecMCP</c> and <c>BiatecOIDC</c>. Google uses
    /// the well-known <c>GoogleOpenIdConnectDefaults.AuthenticationScheme</c> from
    /// <c>Google.Apis.Auth.AspNetCore3</c> (value <c>"Google"</c>, mirrored here as <see cref="Google"/> so
    /// code that only depends on this shared library doesn't need that package reference just to compare
    /// scheme names); Microsoft has no equivalent SDK constant since it's registered via the plain
    /// <c>AddOpenIdConnect</c> handler, so this is the single source of truth for that scheme's name.
    /// </summary>
    public static class AuthSchemeNames
    {
        /// <summary>Scheme name for the Google <c>AddGoogleOpenIdConnect</c> registration.</summary>
        public const string Google = "Google";

        /// <summary>Scheme name for the Microsoft Entra ID <c>AddOpenIdConnect</c> registration.</summary>
        public const string Microsoft = "Microsoft";

        /// <summary>
        /// Claim type added to the signed-in principal (in each scheme's <c>OnTokenValidated</c>) recording
        /// which provider - <see cref="Google"/> or <see cref="Microsoft"/> - the user signed in with, so
        /// downstream code knows which storage backend their self-custody account lives in.
        /// </summary>
        public const string IdpClaimType = "biatec_idp";
    }
}
