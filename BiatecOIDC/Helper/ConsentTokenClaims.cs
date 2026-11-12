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

        /// <summary>
        /// The wallet scopes a consent can grant. <c>rekey</c> never travels: it permanently reassigns control of an account, so it is not
        /// something a consent for one action may carry.
        /// </summary>
        public static readonly string[] GrantableWalletScopes = { "sign", "manage-limits" };

        // Identity only. The scope and the wallet claims are rebuilt from the consent, the subject is the consent's own: none of them is copied.
        // The two encrypted provider-token claims are kept deliberately: the wallet needs them to reach the user's cloud storage (they are
        // encrypted for the user and not usable outside this service), and without them an elevated token could not sign anything.
        private static readonly HashSet<string> Identity = new(StringComparer.Ordinal)
        {
            "email", ClaimTypes.Email,
            "name", ClaimTypes.Name,
            "preferred_username",
            "primary_seed_address",
            "email_verified",
            ProviderAccessTokenProtector.ClaimType,
            ProviderAccessTokenProtector.RefreshClaimType,
            AuthSchemeNames.IdpClaimType,
        };

        private const int MaxSnapshotClaims = 24;
        private const int MaxSnapshotValueLength = 8192;

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
                if (Identity.Contains(type) && value?.ToString() is { Length: > 0 } text)
                {
                    result.Add(new ClaimSnapshot { Type = type, Value = text });
                }
            }
            return result;
        }

        /// <summary>True when <paramref name="type"/> is an identity claim that may be copied into an elevated token.</summary>
        public static bool IsIdentityClaim(string type) => Identity.Contains(type);

        /// <summary>Why a snapshot handed to a consent request is not acceptable, or null: identity claims only, once each, bounded.</summary>
        public static string? Validate(IReadOnlyCollection<ClaimSnapshot> snapshot)
        {
            if (snapshot.Count > MaxSnapshotClaims)
            {
                return "too many token claims";
            }
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var c in snapshot)
            {
                if (!Identity.Contains(c.Type) || string.IsNullOrEmpty(c.Value) || c.Value.Length > MaxSnapshotValueLength || !seen.Add(c.Type))
                {
                    return "token claims must be identity claims, once each";
                }
            }
            return null;
        }
    }
}
