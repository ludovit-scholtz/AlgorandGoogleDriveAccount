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
    }

    /// <summary>A single whitelisted OIDC relying-party client.</summary>
    public class JwtIssuerClientConfiguration
    {
        /// <summary>Unique client identifier presented as <c>client_id</c>.</summary>
        public string ClientId { get; set; } = string.Empty;

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
    }

    /// <summary>Successful <c>/token</c> response body.</summary>
    public class OidcTokenResponse
    {
        /// <summary>Bearer access token.</summary>
        public string AccessToken { get; set; } = string.Empty;

        /// <summary>OIDC ID token (JWT) carrying identity claims.</summary>
        public string IdToken { get; set; } = string.Empty;

        /// <summary>Always <c>Bearer</c>.</summary>
        public string TokenType { get; set; } = "Bearer";

        /// <summary>Access token lifetime, in seconds.</summary>
        public int ExpiresIn { get; set; }

        /// <summary>Refresh token, if the client and grant are eligible for one.</summary>
        public string? RefreshToken { get; set; }

        /// <summary>Space-separated scopes actually granted.</summary>
        public string Scope { get; set; } = "openid profile email";
    }
}
