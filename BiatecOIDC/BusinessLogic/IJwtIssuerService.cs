using BiatecOIDC.Model;
using System.Security.Claims;

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

        Task<(bool Success, string? Error, string? ErrorDescription, Dictionary<string, string>? Response)> CreateAuthorizeResponseAsync(OidcAuthorizeRequest request, JwtIssuerClientConfiguration client, ClaimsPrincipal user);
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
