namespace AlgorandGoogleDriveAccount.Model
{
    public class JwtIssuerConfiguration
    {
        public bool Enabled { get; set; } = true;
        public string Issuer { get; set; } = string.Empty;
        public string KeyId { get; set; } = "biatec-main-key";
        public string SigningPrivateKeyPem { get; set; } = string.Empty;
        public int AuthorizationCodeLifetimeSeconds { get; set; } = 120;
        public int AccessTokenLifetimeMinutes { get; set; } = 15;
        public int IdTokenLifetimeMinutes { get; set; } = 15;
        public int RefreshTokenLifetimeDays { get; set; } = 30;
        public bool AllowHttpForLoopbackRedirectUris { get; set; } = true;
        public List<JwtIssuerClientConfiguration> Clients { get; set; } = new();
    }

    public class JwtIssuerClientConfiguration
    {
        public string ClientId { get; set; } = string.Empty;
        public string? ClientSecret { get; set; }
        public List<string> RedirectUris { get; set; } = new();
        public List<string> PostLogoutRedirectUris { get; set; } = new();
        public List<string> AllowedScopes { get; set; } = new() { "openid", "profile", "email" };
    }

    public class OidcAuthorizeRequest
    {
        public string? ClientId { get; set; }
        public string? RedirectUri { get; set; }
        public string? ReturnUrl { get; set; }
        public string ResponseType { get; set; } = "code";
        public string? ResponseMode { get; set; }
        public string Scope { get; set; } = "openid profile email";
        public string? State { get; set; }
        public string? Nonce { get; set; }
    }

    public class OidcTokenRequest
    {
        public string GrantType { get; set; } = string.Empty;
        public string? Code { get; set; }
        public string? RefreshToken { get; set; }
        public string? RedirectUri { get; set; }
        public string? ClientId { get; set; }
        public string? ClientSecret { get; set; }
    }

    public class OidcTokenResponse
    {
        public string AccessToken { get; set; } = string.Empty;
        public string IdToken { get; set; } = string.Empty;
        public string TokenType { get; set; } = "Bearer";
        public int ExpiresIn { get; set; }
        public string? RefreshToken { get; set; }
        public string Scope { get; set; } = "openid profile email";
    }
}
