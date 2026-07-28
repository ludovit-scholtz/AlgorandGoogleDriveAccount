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
  - Supports PKCE (`code_challenge`, `code_challenge_method`) per RFC 7636 — see "PKCE for Public Clients" below.
- `POST /token`
  - Token exchange endpoint.
  - Supports:
    - `grant_type=authorization_code` (accepts `code_verifier` for PKCE)
    - `grant_type=refresh_token`
- `GET /connect/endsession`
  - RP-initiated logout endpoint.
  - Also available as `GET /logout` alias.
  - Supports parameters:
    - `id_token_hint` (recommended)
    - `post_logout_redirect_uri` (recommended)
    - `state` (recommended)
    - `client_id` (recommended for compatibility)
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

Important behavior for Drive consent:

- Login no longer fails when Google Drive access is denied.
- Tokens are still issued for `openid profile email` authentication.
- `algorand_address` is optional and omitted when Drive access is unavailable.
- Integrator apps should treat `algorand_address` as nullable and request incremental consent only when Drive-backed actions are needed.

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
        "https://*.example.com/auth/callback",
        "http://localhost:3000/auth/callback"
      ],
      "PostLogoutRedirectUris": [
        "https://*.example.com/login",
        "http://localhost:3000/login"
      ],
      "AllowedScopes": ["openid", "profile", "email"]
    },
    {
      "ClientId": "my-mobile-app",
      "ClientSecret": null,
      "RedirectUris": [
        "io.example.myapp:/oauth2redirect"
      ],
      "PostLogoutRedirectUris": [
        "io.example.myapp:/oauth2redirect"
      ],
      "AllowedScopes": ["openid", "profile", "email"]
    }
  ]
}
```

A client is a **public client** whenever `ClientSecret` is `null`/empty — this is the correct registration for
Android/iOS/desktop apps and browser SPAs, which cannot keep a secret confidential. PKCE (`code_challenge` /
`code_verifier`) is mandatory for such clients; see "PKCE for Public Clients (Mobile / Desktop Apps)" below.
`RedirectUris` accepts custom (non-`http`/`https`) URI schemes, e.g. `io.example.myapp:/oauth2redirect` for an
Android app-link/custom-scheme redirect — the same allowlist and wildcard rules apply, matched on scheme + host +
port + path.

Notes:

- `SigningPrivateKeyPem` must be an RSA private key in PEM format.
  - Supported PEM headers: `BEGIN PRIVATE KEY` (PKCS#8) and `BEGIN RSA PRIVATE KEY` (PKCS#1)
  - Unsupported format: `BEGIN OPENSSH PRIVATE KEY` (common output of `ssh-keygen`)
- If you provide a file path in `SigningPrivateKeyPem`, the service will read PEM content from that file.
- If `SigningPrivateKeyPem` is empty, service falls back to ephemeral key (not for production).
- Redirect URIs are allowlisted and support `*` wildcards in the configured host, path, and query.
  - Example: `https://*.example.com/auth/callback` matches `https://tenant-a.example.com/auth/callback`.
  - `https://*.example.com/auth/callback` does not match `https://example.com/auth/callback`; register the root domain separately when needed.
  - Scheme and port must still match exactly.
- Post-logout redirect URIs are allowlisted via `PostLogoutRedirectUris` with the same wildcard rules.
  - If `PostLogoutRedirectUris` is empty for a client, `RedirectUris` are used as fallback allowlist for logout redirects.

### Generate compatible signing key (recommended)

Use OpenSSL to generate a PEM key the service can import:

```bash
openssl genpkey -algorithm RSA -pkeyopt rsa_keygen_bits:4096 -out jwt-signing-private.pem
openssl rsa -pubout -in jwt-signing-private.pem -out jwt-signing-public.pem
```

Then configure either:

1. Inline PEM with escaped newlines (`\\n`) in `SigningPrivateKeyPem`
2. A file path to `jwt-signing-private.pem` in `SigningPrivateKeyPem`

### Ed25519 / EdDSA note

`Ed25519` (`EdDSA`) is not currently wired in this service. The current JWT token stack used by this project is configured for `RS256` issuance and validation.

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

## PKCE for Public Clients (Mobile / Desktop Apps)

Native apps (Android, iOS, desktop) and browser SPAs cannot store a `client_secret` confidentially — anyone can
decompile the app or read the bundle. Register these as a **public client** (`ClientSecret: null` in
`JwtIssuer:Clients`) and use PKCE (RFC 7636) instead of a client secret to protect the authorization code exchange.
The server rejects `response_type=code` authorization requests from a public client if `code_challenge` is missing.

This is the standard flow for an Android app using AppAuth (or an equivalent PKCE-capable OIDC library):

1. Generate a random `code_verifier` (43–128 chars, unreserved charset `[A-Za-z0-9-._~]`) and derive
   `code_challenge = BASE64URL(SHA256(code_verifier))` (`code_challenge_method=S256`; use `S256`, not `plain`, in
   production — `plain` exists only for clients that cannot compute SHA-256).
2. Open the system browser / Custom Tab to:

```text
GET https://google.biatec.io/authorize
  ?client_id=my-mobile-app
  &redirect_uri=io.example.myapp%3A%2Foauth2redirect
  &response_type=code
  &scope=openid%20profile%20email
  &state=<csrf_random>
  &nonce=<nonce_random>
  &code_challenge=<code_challenge>
  &code_challenge_method=S256
```

3. The user authenticates with Google at Biatec (same flow as the web case — the app never sees Google
   credentials).
4. Biatec redirects to the app's registered custom-scheme `redirect_uri` with `code` and `state`. The OS routes
   this back into the app (Android App Links / custom scheme intent filter).
5. The app exchanges the code directly from the device — no `client_secret`, no backend needed:

```text
POST https://google.biatec.io/token
Content-Type: application/x-www-form-urlencoded

grant_type=authorization_code
&code=<code>
&redirect_uri=io.example.myapp%3A%2Foauth2redirect
&client_id=my-mobile-app
&code_verifier=<code_verifier>
```

6. Validate the returned `id_token` the same way as the confidential-client flow (issuer, `jwks_uri`, audience,
   expiration, signature) and store `access_token`/`refresh_token` in platform-appropriate secure storage
   (Android Keystore-backed `EncryptedSharedPreferences`, iOS Keychain, etc.) — never in plain files or logs.
7. Refresh with `grant_type=refresh_token` at `/token` exactly like a confidential client; refresh does not require
   `code_verifier` (PKCE only protects the authorization code step).

Notes:

- `code_challenge_method` accepts `S256` (recommended) or `plain`.
- PKCE is optional (but still accepted and validated if sent) for confidential clients that have a `ClientSecret`
  configured — it does not replace the secret for those clients, it complements it.
- The token endpoint remains public/anonymous (`token_endpoint_auth_methods_supported` includes `none`) precisely
  to support this public-client, secret-less exchange; PKCE is what prevents a stolen authorization code from being
  redeemed by an attacker.

## RP-Initiated Logout Flow (Required for full sign-out)

Use standards-based RP-initiated logout so the Biatec IdP session is cleared, not just the local app session.

Dedicated requirements doc for Capitalism integrators:
- `BIATEC_OIDC_LOGOUT_REQUIREMENTS.md`

1. Clear your application session.
2. Redirect browser to:

```text
GET https://google.biatec.io/connect/endsession
  ?id_token_hint=<last_id_token>
  &post_logout_redirect_uri=https%3A%2F%2Fmy-app.example.com%2Flogin
  &state=<csrf_or_logout_state>
  &client_id=my-app
```

3. Biatec invalidates its authentication session cookie.
4. Browser is redirected to `post_logout_redirect_uri`.
5. `state` is preserved and returned as query parameter when provided.

Notes:

- `post_logout_redirect_uri` must be absolute and allowlisted for the client.
- Allowlist matching is based on scheme + host + port + path, with optional `*` wildcards in configured entries.
- Query parameters are allowed on top of an allowlisted base URI. Wildcards in `PostLogoutRedirectUris` are evaluated before query parameters are appended.
- `https://*.example.com/login` matches `https://tenant-a.example.com/login` but not `https://example.com/login`.
- For best interoperability, send both `id_token_hint` and `client_id`.
- Discovery metadata includes `end_session_endpoint` for dynamic client configuration.
- Capitalism frontend environment variable:
  - `VITE_BIATEC_OIDC_END_SESSION_URL=https://google.biatec.io/connect/endsession`

Example accepted logout redirect:

```text
Allowlisted base URI: http://localhost:5173/login
Runtime URI:          http://localhost:5173/login?redirect=%2F&oidc_retry=consent
```

This runtime URI is valid because it matches the allowlisted origin and path.

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
- Register mobile/desktop/SPA clients as public clients (no `ClientSecret`) and require PKCE — never embed a
  `client_secret` in an app binary or frontend bundle.
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
- Discovery contains `end_session_endpoint`.
- Logout via `/connect/endsession` redirects to allowlisted `post_logout_redirect_uri`.
- A new login after logout requires a fresh Biatec authentication session.
- For a public (PKCE) client: `/authorize` without `code_challenge` returns `invalid_request`.
- For a public (PKCE) client: `/token` with a correct `code` but missing/wrong `code_verifier` returns
  `invalid_grant`; the matching `code_verifier` succeeds without any `client_secret`.
