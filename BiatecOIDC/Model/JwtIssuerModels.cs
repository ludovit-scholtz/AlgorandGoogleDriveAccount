using System.Text.Json.Serialization;

namespace BiatecOIDC.Model
{
    /// <summary>OIDC/JWT identity provider settings, bound from the <c>JwtIssuer</c> configuration section.</summary>
    public class JwtIssuerConfiguration
    {
        /// <summary>Whether the OIDC issuer endpoints are enabled.</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>The <c>iss</c> claim value and discovery document issuer URL.</summary>
        public string Issuer { get; set; } = string.Empty;

        /// <summary>Key ID (<c>kid</c>) advertised in issued tokens and the JWKS document.</summary>
        public string KeyId { get; set; } = "biatec-main-key";

        /// <summary>
        /// RS256 private signing key, as PKCS#8 or PKCS#1 PEM (escaped <c>\n</c>), or a file path to such a PEM file.
        /// </summary>
        public string SigningPrivateKeyPem { get; set; } = string.Empty;

        /// <summary>How long an issued authorization code remains valid.</summary>
        public int AuthorizationCodeLifetimeSeconds { get; set; } = 120;

        /// <summary>How long an issued access token remains valid.</summary>
        public int AccessTokenLifetimeMinutes { get; set; } = 15;

        /// <summary>How long an issued ID token remains valid.</summary>
        public int IdTokenLifetimeMinutes { get; set; } = 15;

        /// <summary>How long an issued refresh token remains valid.</summary>
        public int RefreshTokenLifetimeDays { get; set; } = 30;

        /// <summary>Whether <c>http://</c> loopback redirect URIs (e.g. <c>http://127.0.0.1:PORT/...</c>) are allowed for native-app clients.</summary>
        public bool AllowHttpForLoopbackRedirectUris { get; set; } = true;

        /// <summary>The whitelisted set of relying-party clients allowed to use this issuer.</summary>
        public List<JwtIssuerClientConfiguration> Clients { get; set; } = new();

        /// <summary>
        /// Canonical resource-server URIs (RFC 8707) this authorization server will vouch for via the
        /// <c>resource</c> parameter on <c>/authorize</c>/<c>/token</c> (e.g.
        /// <c>["https://mcp.biatec.io/mcp"]</c>). Exists for resource servers - like BiatecMCP - that
        /// accept tokens from many different dynamically-registered clients (see
        /// <see cref="DynamicClientRegistrationDefaultScopes"/>) and therefore cannot pin JWT audience
        /// validation to one fixed <c>client_id</c>: a validated <c>resource</c> is added to the issued
        /// token's <c>aud</c> claim alongside <c>client_id</c>, so the resource server can validate against
        /// this one stable value regardless of which client obtained the token. A <c>resource</c> value not
        /// in this list is rejected (<c>invalid_target</c>) rather than silently ignored. Existing clients
        /// that never send <c>resource</c> are entirely unaffected - <c>aud</c> stays exactly
        /// <c>[client_id]</c>, as before this existed.
        /// </summary>
        public List<string> ProtectedResources { get; set; } = new();

        /// <summary>
        /// Scopes granted to a client created via <c>POST /register</c> (RFC 7591 Dynamic Client
        /// Registration - see <c>IDynamicClientStore</c>), regardless of what the registration request
        /// asked for. Deliberately excludes <c>manage-limits</c>/<c>rekey</c> - the two highest-privilege
        /// wallet scopes - so a self-registered client can never obtain them without an operator
        /// hand-upgrading that specific client_id afterwards (adding a static <c>Clients</c> entry with the
        /// same <c>ClientId</c>, which always takes precedence over a dynamic registration - see
        /// <c>IJwtIssuerService.ResolveClientAsync</c>).
        /// </summary>
        public List<string> DynamicClientRegistrationDefaultScopes { get; set; } = new() { "openid", "profile", "email", "sign" };
    }

    /// <summary>A single whitelisted OIDC relying-party client.</summary>
    public class JwtIssuerClientConfiguration
    {
        /// <summary>Unique client identifier presented as <c>client_id</c>.</summary>
        public string ClientId { get; set; } = string.Empty;

        /// <summary>
        /// Human-friendly app name shown to the user on the provider-picker and consent screens (e.g.
        /// "Capitalism 5"), instead of the raw <see cref="ClientId"/> (e.g. "capitalism-pkce"). Falls back
        /// to <see cref="ClientId"/> when left blank, so existing clients that never set this keep working
        /// unchanged - just with a less polished label until it's filled in.
        /// </summary>
        public string? DisplayName { get; set; }

        /// <summary>Client secret for confidential clients. Null/empty makes this a public client (see <c>IsPublicClient</c>).</summary>
        public string? ClientSecret { get; set; }

        /// <summary>Allowlisted redirect URIs for the authorization code flow (supports <c>*</c> wildcard segments).</summary>
        public List<string> RedirectUris { get; set; } = new();

        /// <summary>Allowlisted post-logout redirect URIs. Falls back to <c>RedirectUris</c> when empty.</summary>
        public List<string> PostLogoutRedirectUris { get; set; } = new();

        /// <summary>Scopes this client is permitted to request.</summary>
        public List<string> AllowedScopes { get; set; } = new() { "openid", "profile", "email" };

        /// <summary>
        /// A client with no ClientSecret is a public client (SPA, mobile, desktop) and cannot keep a secret
        /// confidential, so PKCE (RFC 7636) is mandatory for it on the authorization_code grant.
        /// </summary>
        public bool IsPublicClient => string.IsNullOrWhiteSpace(ClientSecret);
    }

    /// <summary>Normalized parameters of an incoming <c>/authorize</c> request.</summary>
    public class OidcAuthorizeRequest
    {
        /// <summary>Registered client identifier.</summary>
        public string? ClientId { get; set; }

        /// <summary>Redirect URI for the standard <c>response_type=code</c> flow.</summary>
        public string? RedirectUri { get; set; }

        /// <summary>Return URL for the legacy direct <c>id_token</c> flow.</summary>
        public string? ReturnUrl { get; set; }

        /// <summary>Requested OIDC response type; only <c>code</c> is supported for the standard flow.</summary>
        public string ResponseType { get; set; } = "code";

        /// <summary>How the authorization result is delivered (e.g. <c>query</c> or <c>form_post</c>).</summary>
        public string? ResponseMode { get; set; }

        /// <summary>Space-separated requested scopes.</summary>
        public string Scope { get; set; } = "openid profile email";

        /// <summary>Opaque value round-tripped back to the client.</summary>
        public string? State { get; set; }

        /// <summary>Value round-tripped into the issued <c>id_token</c>.</summary>
        public string? Nonce { get; set; }

        /// <summary>PKCE code challenge (RFC 7636).</summary>
        public string? CodeChallenge { get; set; }

        /// <summary>PKCE code challenge method: <c>S256</c> or <c>plain</c>.</summary>
        public string? CodeChallengeMethod { get; set; }

        /// <summary>
        /// RFC 8707 resource indicator - the canonical URI of the resource server the requested token must
        /// be valid for (e.g. <c>https://mcp.biatec.io/mcp</c>). Optional; when present it must match an
        /// entry in <see cref="JwtIssuerConfiguration.ProtectedResources"/> or the request is rejected
        /// (<c>invalid_target</c>). See that property's remarks for why this exists.
        /// </summary>
        public string? Resource { get; set; }
    }

    /// <summary>Normalized parameters of an incoming <c>/token</c> request.</summary>
    public class OidcTokenRequest
    {
        /// <summary><c>authorization_code</c> or <c>refresh_token</c>.</summary>
        public string GrantType { get; set; } = string.Empty;

        /// <summary>Authorization code issued by <c>/authorize</c>, for the authorization_code grant.</summary>
        public string? Code { get; set; }

        /// <summary>Refresh token, for the refresh_token grant.</summary>
        public string? RefreshToken { get; set; }

        /// <summary>Must match the <c>redirect_uri</c> originally used to obtain the authorization code.</summary>
        public string? RedirectUri { get; set; }

        /// <summary>Client identifier, if not authenticated via HTTP Basic.</summary>
        public string? ClientId { get; set; }

        /// <summary>Client secret, if not authenticated via HTTP Basic.</summary>
        public string? ClientSecret { get; set; }

        /// <summary>PKCE code verifier matching the <c>code_challenge</c> supplied to <c>/authorize</c>.</summary>
        public string? CodeVerifier { get; set; }

        /// <summary>Same RFC 8707 resource indicator as <see cref="OidcAuthorizeRequest.Resource"/> - must match what was requested at <c>/authorize</c>.</summary>
        public string? Resource { get; set; }
    }

    /// <summary>Request body for <c>POST /register</c> (RFC 7591 Dynamic Client Registration, public clients only).</summary>
    public class DynamicClientRegistrationRequest
    {
        /// <summary>Human-readable client name, for display on consent/picker screens. Optional.</summary>
        [JsonPropertyName("client_name")]
        public string? ClientName { get; set; }

        /// <summary>At least one redirect URI is required - each must be HTTPS or a loopback HTTP URI.</summary>
        [JsonPropertyName("redirect_uris")]
        public List<string> RedirectUris { get; set; } = new();

        /// <summary>
        /// Must be <c>"none"</c> or omitted - this endpoint only ever registers public clients (no secret
        /// is ever generated by it). Any other value is rejected.
        /// </summary>
        [JsonPropertyName("token_endpoint_auth_method")]
        public string? TokenEndpointAuthMethod { get; set; }

        /// <summary>Requested scopes - capped server-side to <see cref="JwtIssuerConfiguration.DynamicClientRegistrationDefaultScopes"/> regardless of what's asked for here. Optional.</summary>
        [JsonPropertyName("scope")]
        public string? Scope { get; set; }
    }

    /// <summary>Response body for <c>POST /register</c> (RFC 7591 shape).</summary>
    public class DynamicClientRegistrationResponse
    {
        [JsonPropertyName("client_id")]
        public string ClientId { get; set; } = string.Empty;

        [JsonPropertyName("client_id_issued_at")]
        public long ClientIdIssuedAt { get; set; }

        [JsonPropertyName("redirect_uris")]
        public List<string> RedirectUris { get; set; } = new();

        [JsonPropertyName("token_endpoint_auth_method")]
        public string TokenEndpointAuthMethod { get; set; } = "none";

        [JsonPropertyName("grant_types")]
        public List<string> GrantTypes { get; set; } = new() { "authorization_code", "refresh_token" };

        [JsonPropertyName("response_types")]
        public List<string> ResponseTypes { get; set; } = new() { "code" };

        [JsonPropertyName("scope")]
        public string Scope { get; set; } = string.Empty;
    }

    /// <summary>
    /// Successful <c>/token</c> response body. Property names are pinned with
    /// <see cref="JsonPropertyNameAttribute"/> to the exact snake_case wire names RFC 6749/OIDC Core
    /// require (<c>access_token</c>, etc.) - relying on ASP.NET Core's default camelCase JSON naming
    /// policy would instead emit <c>accessToken</c>, which OAuth/OIDC clients (including Postman's
    /// built-in token extraction) don't recognize.
    /// </summary>
    public class OidcTokenResponse
    {
        /// <summary>Bearer access token.</summary>
        [JsonPropertyName("access_token")]
        public string AccessToken { get; set; } = string.Empty;

        /// <summary>OIDC ID token (JWT) carrying identity claims.</summary>
        [JsonPropertyName("id_token")]
        public string IdToken { get; set; } = string.Empty;

        /// <summary>Always <c>Bearer</c>.</summary>
        [JsonPropertyName("token_type")]
        public string TokenType { get; set; } = "Bearer";

        /// <summary>Access token lifetime, in seconds.</summary>
        [JsonPropertyName("expires_in")]
        public int ExpiresIn { get; set; }

        /// <summary>Refresh token, if the client and grant are eligible for one.</summary>
        [JsonPropertyName("refresh_token")]
        public string? RefreshToken { get; set; }

        /// <summary>Space-separated scopes actually granted.</summary>
        [JsonPropertyName("scope")]
        public string Scope { get; set; } = "openid profile email";
    }
}
