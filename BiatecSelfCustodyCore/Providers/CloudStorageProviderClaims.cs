using System.Security.Claims;
using BiatecSelfCustodyCore.Model;

namespace BiatecSelfCustodyCore.Providers
{
    /// <summary>
    /// Stamps the <see cref="AuthSchemeNames.IdpClaimType"/> claim onto a freshly signed-in principal,
    /// recording which <see cref="ICloudStorageProvider"/> the user authenticated with. Called from
    /// each app's <c>OnTokenValidated</c> handler for every registered provider's OIDC scheme - see
    /// <c>BiatecMCP/Program.cs</c>/<c>BiatecOIDC/Program.cs</c> for the pattern to copy when wiring up
    /// a new provider.
    /// </summary>
    public static class CloudStorageProviderClaims
    {
        public static void Stamp(ClaimsPrincipal? principal, string providerName)
        {
            (principal?.Identity as ClaimsIdentity)?.AddClaim(new Claim(AuthSchemeNames.IdpClaimType, providerName));
        }
    }
}
