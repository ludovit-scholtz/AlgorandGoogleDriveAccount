namespace BiatecMCP.Model
{
    /// <summary>
    /// The BiatecOIDC authorization server this MCP server (an OAuth 2.1 resource server) delegates
    /// authentication and signing to. Bound from the <c>Oidc</c> configuration section.
    /// </summary>
    public class OidcConfiguration
    {
        /// <summary>
        /// BiatecOIDC's issuer base URL (e.g. <c>https://oidc.biatec.io</c>) - used both as the
        /// <c>Authority</c> for local JWT bearer-token validation (JWKS/discovery are fetched from here)
        /// and as the base URL for the wallet REST API calls (<c>/wallet/sign</c>, <c>/wallet/seeds</c>)
        /// this service forwards the caller's own bearer token to.
        /// </summary>
        public string Issuer { get; set; } = "https://oidc.biatec.io";
    }
}
