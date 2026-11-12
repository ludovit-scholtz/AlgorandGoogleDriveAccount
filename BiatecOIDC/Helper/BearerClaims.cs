using System.Security.Claims;

namespace BiatecOIDC.Helper
{
    /// <summary>
    /// Reads a claim of a validated access token. <c>JwtSecurityTokenHandler</c> maps some claim types on the way in (<c>sub</c> becomes
    /// the long NameIdentifier URI, <c>acr</c> the AuthnClassReference URI), so <c>principal.FindFirstValue("sub")</c> is null for a real
    /// token. The raw claims of the token (as <c>ValidateBearerAccessToken</c> returns them) are authoritative; the principal is only a fallback.
    /// </summary>
    public static class BearerClaims
    {
        public static string? Value((bool IsValid, ClaimsPrincipal? Principal, IDictionary<string, object>? Claims, string? Error) validation, string type)
        {
            if (!validation.IsValid)
            {
                return null;
            }
            if (validation.Claims is { } raw && raw.TryGetValue(type, out var value) && value?.ToString() is { Length: > 0 } text)
            {
                return text;
            }
            var principal = validation.Principal;
            return principal?.FindFirstValue(type) ?? (type == "sub" ? principal?.FindFirstValue(ClaimTypes.NameIdentifier) : null);
        }
    }
}
