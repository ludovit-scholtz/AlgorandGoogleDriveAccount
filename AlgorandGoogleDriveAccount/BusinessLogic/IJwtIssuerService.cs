using AlgorandGoogleDriveAccount.Model;
using System.Security.Claims;

namespace AlgorandGoogleDriveAccount.BusinessLogic
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
    }
}
