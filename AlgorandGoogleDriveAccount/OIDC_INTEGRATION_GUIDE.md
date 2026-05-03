# Biatec OIDC and JWT Integration Guide

This project now exposes a standards-oriented OpenID Connect style identity provider so other applications can delegate login to Biatec Google authentication and receive signed JWT tokens containing Algorand identity claims.

## Goals

- Reuse Google login session from this service.
- Issue JWT tokens signed by Biatec signing key.
- Include Algorand address and email claims in issued tokens.
- Keep redirect handling allowlisted per application client.

## Implemented Endpoints

- `GET /.well-known/openid-configuration`
  - Discovery document for clients.
- `GET /.well-known/jwks.json`
  - Public signing keys for JWT validation.
- `GET /authorize`
  - Authorization endpoint.
  - Supports:
    - `response_type=code` (recommended, server to server token exchange)
    - `response_type=id_token` (legacy style direct token return)
  - Supports `response_mode=query` and `response_mode=form_post`.
  - Supports legacy `returnUrl` alias for `redirect_uri`.
- `POST /token`
  - Token exchange endpoint.
  - Supports:
    - `grant_type=authorization_code`
    - `grant_type=refresh_token`
- `GET /userinfo`
  - Returns user claims from bearer access token.
- `POST /introspect`
  - RFC-like active token introspection response.
- `POST /verify`
  - Convenience token verification endpoint.

## Important Claims in Tokens

ID token and access token contain these relevant claims:

- `sub`: pairwise subject per client and email.
- `email`: authenticated Google email.
- `name` and `preferred_username`: shortened Algorand identity (first 4 + last 4 chars from account address).
- `algorand_address`: full Algorand account address.
- Standard claims: `iss`, `aud`, `exp`, `iat`, `nbf`, `jti`.

## Configuration

Configure `JwtIssuer` in `appsettings.json`.

```json
"JwtIssuer": {
  "Enabled": true,
  "Issuer": "https://google.biatec.io",
  "KeyId": "biatec-main-key",
  "SigningPrivateKeyPem": "-----BEGIN PRIVATE KEY-----\\n...\\n-----END PRIVATE KEY-----",
  "AuthorizationCodeLifetimeSeconds": 120,
  "AccessTokenLifetimeMinutes": 15,
  "IdTokenLifetimeMinutes": 15,
  "RefreshTokenLifetimeDays": 30,
  "AllowHttpForLoopbackRedirectUris": true,
  "Clients": [
    {
      "ClientId": "my-app",
      "ClientSecret": "super-strong-secret",
      "RedirectUris": [
        "https://my-app.example.com/auth/callback",
        "http://localhost:3000/auth/callback"
      ],
      "AllowedScopes": ["openid", "profile", "email"]
    }
  ]
}
```

Notes:

- `SigningPrivateKeyPem` must be RSA private key in PEM format.
- If `SigningPrivateKeyPem` is empty, service falls back to ephemeral key (not for production).
- Redirect URIs are exact-match allowlisted.

## Recommended Flow for Destination Project

Use authorization code flow.

1. Redirect browser to:

```text
GET https://google.biatec.io/authorize
  ?client_id=my-app
  &redirect_uri=https%3A%2F%2Fmy-app.example.com%2Fauth%2Fcallback
  &response_type=code
  &scope=openid%20profile%20email
  &state=<csrf_random>
  &nonce=<nonce_random>
```

2. User authenticates with Google at Biatec.
3. Biatec redirects back to your `redirect_uri` with `code` and `state`.
4. Your backend exchanges code at token endpoint:

```text
POST https://google.biatec.io/token
Content-Type: application/x-www-form-urlencoded
Authorization: Basic base64(client_id:client_secret)

grant_type=authorization_code
&code=<code>
&redirect_uri=https%3A%2F%2Fmy-app.example.com%2Fauth%2Fcallback
```

5. Validate ID token with:
   - issuer from discovery
   - jwks from `jwks_uri`
   - audience = your client id
   - expiration and signature
6. Use `refresh_token` at `/token` with `grant_type=refresh_token` when renewing.

## Legacy Direct Token POST Flow

If needed, a direct token return is available:

```text
GET /authorize?returnUrl=https%3A%2F%2Fmy-app.example.com%2Fauth%2Fcallback
```

This path defaults to `response_type=id_token` and `response_mode=form_post` for compatibility.

## Security Recommendations

- Use HTTPS in production for all endpoints and redirect URIs.
- Keep authorization codes short lived.
- Always validate `state` for CSRF protection.
- Use strong client secrets for confidential clients.
- Rotate signing keys using `kid` changes and serve both old and new public keys during transition.
- Validate `iss`, `aud`, and signature on every token.

## Copilot Prompt for Destination Project

Use this prompt in your destination project so Copilot can scaffold integration quickly:

```text
Implement OpenID Connect authorization code login against issuer https://google.biatec.io.
Requirements:
- Discover metadata from /.well-known/openid-configuration.
- Start login by redirecting to /authorize with client_id, redirect_uri, response_type=code, scope=openid profile email, state, nonce.
- Handle callback, validate state, exchange code at /token with client_secret_basic.
- Validate id_token using jwks from jwks_uri (RS256, kid aware).
- Map claims: email, preferred_username, algorand_address.
- Store refresh_token securely and implement token refresh with grant_type=refresh_token.
- Add middleware/guard to reject invalid issuer, audience, signature, and expired tokens.
- Add unit tests for callback state validation and id_token signature validation.
```

## Validation Checklist

- Discovery document resolves and contains issuer, authorize, token, jwks endpoints.
- Redirect URI is allowlisted exactly.
- `/authorize` triggers Google login if no session cookie exists.
- `/token` returns access token, id token, refresh token for valid code.
- `/userinfo` returns expected claims for valid access token.
- `/introspect` returns `active=true` for valid access token.
