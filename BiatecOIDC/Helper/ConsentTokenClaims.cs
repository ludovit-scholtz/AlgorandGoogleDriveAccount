using System.Security.Claims;
using BiatecOIDC.BusinessLogic;
using BiatecOIDC.Model;
using BiatecSelfCustodyCore.Model;

namespace BiatecOIDC.Helper
{
    /// <summary>
    /// The claims of the caller's own access token that are carried over into the elevated (consent) token, and nothing else.
    /// An allowlist, not a blocklist: a claim added to access tokens later does not silently travel into a consent token.
    /// </summary>
    public static class ConsentTokenClaims
    {
        /// <summary>The <c>acr</c> of an elevated token (RFC 9470).</summary>
        public const string Acr = "biatec:consent";

        public const string AuthReqIdClaim = "consent_auth_req_id";
        public const string HashClaim = "consent_hash";

        private static readonly HashSet<string> Allowed = new(StringComparer.Ordinal)
        {
            "sub", ClaimTypes.NameIdentifier,
            "email", ClaimTypes.Email,
            "name", ClaimTypes.Name,
            "preferred_username",
            "scope",
            "primary_seed_address",
            "email_verified",
            "sign", "manage-limits", "rekey",
            ProviderAccessTokenProtector.ClaimType,
            ProviderAccessTokenProtector.RefreshClaimType,
            AuthSchemeNames.IdpClaimType,
        };

        /// <summary>
        /// The allowlisted claims, once each, for storing with a consent request. Taken from the <b>raw</b> claims of the challenged token
        /// (what <c>ValidateBearerAccessToken</c> returns as its claims dictionary), whose types are exactly what was written into the token:
        /// the validated principal has them mapped (<c>sub</c> becomes NameIdentifier), and writing a mapped type back would change the token.
        /// </summary>
        public static List<ClaimSnapshot> Snapshot(IDictionary<string, object> rawClaims)
        {
            var result = new List<ClaimSnapshot>();
            foreach (var (type, value) in rawClaims)
            {
                if (Allowed.Contains(type) && value?.ToString() is { Length: > 0 } text)
                {
                    result.Add(new ClaimSnapshot { Type = type, Value = text });
                }
            }
            return result;
        }

        /// <summary>True when <paramref name="type"/> may be copied into an elevated token.</summary>
        public static bool IsAllowed(string type) => Allowed.Contains(type);
    }
}
