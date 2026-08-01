using System.Security.Claims;
using BiatecOIDC.Model;

namespace BiatecOIDC.BusinessLogic
{
    public interface IJwtIssuerService
    {
        string GetIssuer(HttpRequest request);
        object GetDiscoveryDocument(HttpRequest request);
        object GetJsonWebKeySet();

        Task<(bool IsValid, string? Error, string? ErrorDescription, OidcAuthorizeRequest? NormalizedRequest, JwtIssuerClientConfiguration? Client)> ValidateAuthorizeRequestAsync(OidcAuthorizeRequest request);
        Task<string> StorePendingAuthorizeRequestAsync(OidcAuthorizeRequest request);
        Task<OidcAuthorizeRequest?> GetPendingAuthorizeRequestAsync(string requestId);
        Task RemovePendingAuthorizeRequestAsync(string requestId);

        /// <summary>
        /// Non-destructive read of a pending authorize request - unlike <see cref="GetPendingAuthorizeRequestAsync"/>,
        /// does not consume it. Used to render the consent screen (<c>/authorize/consent</c>) without
        /// invalidating the request the user still needs to confirm or that an unattended auto-continue
        /// still needs to finalize.
        /// </summary>
        Task<OidcAuthorizeRequest?> PeekPendingAuthorizeRequestAsync(string requestId);

        /// <param name="providerAccessToken">
        /// The caller's current live Google/Microsoft access token, if available (resolved from the
        /// ambient cookie session at the call site) - encrypted and cached inside the issued access token
        /// (and carried through refreshes) so wallet-API callers don't have to separately manage/resend
        /// it. Pass <c>null</c> if unavailable; the issued token simply won't carry a cached provider
        /// token, and callers fall back to supplying their own explicitly. See
        /// <c>OIDC_INTEGRATION_GUIDE.md</c>'s "Provider access token caching" section.
        /// </param>
        /// <param name="providerRefreshToken">
        /// The caller's current Google/Microsoft refresh token, if available (resolved from the ambient
        /// cookie session the same way as <paramref name="providerAccessToken"/>) - encrypted and cached
        /// the same way, so <c>providerAccessToken</c> can be renewed once it expires instead of requiring
        /// a fresh interactive sign-in every time. Pass <c>null</c> if unavailable.
        /// </param>
        Task<(bool Success, string? Error, string? ErrorDescription, Dictionary<string, string>? Response)> CreateAuthorizeResponseAsync(OidcAuthorizeRequest request, JwtIssuerClientConfiguration client, ClaimsPrincipal user, string? providerAccessToken, string? providerRefreshToken);
        Task<(bool Success, int StatusCode, string? Error, string? ErrorDescription, OidcTokenResponse? Response)> ExchangeTokenAsync(OidcTokenRequest request, string? basicAuthHeader);

        (bool IsValid, ClaimsPrincipal? Principal, IDictionary<string, object>? Claims, string? Error) ValidateBearerAccessToken(string token);
        Task<Dictionary<string, object>> IntrospectAsync(string token);

        /// <summary>
        /// Validates the signature and issuer of a token previously issued by this provider (e.g. an
        /// <c>id_token_hint</c> at logout) and returns its <c>aud</c> claim if valid. Lifetime is not
        /// checked, since a logout hint legitimately references an already-expired id_token. Returns
        /// <c>null</c> if the token's signature/issuer don't validate, so callers never trust an
        /// unauthenticated claim from caller-supplied input.
        /// </summary>
        string? TryGetAudienceFromSelfIssuedToken(string token);
    }
}
