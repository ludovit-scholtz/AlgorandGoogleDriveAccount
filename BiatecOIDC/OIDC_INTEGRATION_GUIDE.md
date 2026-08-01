# Biatec OIDC and JWT Integration Guide

This project now exposes a standards-oriented OpenID Connect style identity provider so other applications can delegate login to Biatec Google authentication and receive signed JWT tokens containing Algorand identity claims.

**Host**: `https://oidc.biatec.io` is the recommended host for new integrations — use it below. The
service is also still reachable at `https://google.biatec.io` (its original, shared host, kept
working as a legacy alias for existing integrations). Each host is internally self-consistent: the
`iss` claim and the discovery document's `issuer` field always match whichever host you actually
call — they are derived from the request, not hardcoded — so pick one host and use it consistently
for a given client (don't mix the two for the same integration).

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
- `POST /wallet/sign`
  - Signs an Algorand transaction group. Requires the `sign` scope (see "Wallet API" below).
- `GET /wallet/limits`
  - Reads the caller's own daily/weekly/monthly spending limits. Only requires being authenticated
    (`openid`) - no `manage-limits` scope needed to read your own limits.
- `PUT /wallet/limits`
  - Sets the caller's own daily/weekly/monthly spending limits and their currency. Requires the
    `manage-limits` scope.
- `GET /wallet/limits/currencies`
  - Lists every currency a spending limit can be configured in, with its current USD exchange rate. Only
    requires being authenticated.

## Important Claims in Tokens

ID token and access token contain these relevant claims:

- `sub`: pairwise subject per client and email.
- `email`: authenticated Google email.
- `name` and `preferred_username`: shortened Algorand identity (first 4 + last 4 chars from account address).
- `algorand_address`: full Algorand account address.
- Standard claims: `iss`, `aud`, `exp`, `iat`, `nbf`, `jti`.

Access tokens additionally carry, when applicable:

- `biatec_idp`: which provider (`Google`/`Microsoft`) the wallet is stored under.
- `sign`: `"true"` — only present if the `sign` scope was requested and is allowlisted for your client.
- `manage-limits`: `"true"` — only present if the `manage-limits` scope was requested and allowlisted.
- `provider_token`: an encrypted (never plaintext) copy of your Google/Microsoft access token, cached so wallet
  API calls don't need to separately supply one — only present when one was available to cache at issuance time.
  Opaque to you (and to any relying party inspecting the token) — only this service can decrypt it. See "Provider
  access token caching" below.

Important behavior for Drive consent:

- Login no longer fails when Google Drive access is denied.
- Tokens are still issued for `openid profile email` authentication.
- `algorand_address` is optional and omitted when Drive access is unavailable.
- Integrator apps should treat `algorand_address` as nullable and request incremental consent only when Drive-backed actions are needed.

## Scope handling

`/authorize` treats `scope` as a *negotiation*, not an all-or-nothing contract - the only way a request fails
over scope is if `openid` itself is missing (`invalid_scope`, `"The openid scope is required."`). Every other
requested scope is either granted or silently dropped:

- `openid`, `profile`, and `email` — basic identity, always granted to every registered client, regardless of
  its `AllowedScopes` list.
- `sign`, `manage-limits` — the wallet-API scopes. Granted only if the client's `AllowedScopes` includes them
  (see "Client registration" below); otherwise dropped from the grant.
- Anything else — a typo, a scope this server has never heard of, a scope your OIDC library auto-appends whether
  you asked for it or not (e.g. some MSAL-flavored clients send a literal `.default` scope when none is
  explicitly configured) — is dropped the same way. There's nothing to configure to make this work; unrecognized
  scopes just don't do anything.

The scope you actually end up with is always visible in the token response's `scope` field (and, for an
authorization-code exchange, the same value the `/token` response returns) - if `/wallet/sign` or
`/wallet/limits` unexpectedly returns `403 insufficient_scope`, check that field (or `GET /userinfo`, whose
underlying access token carries the `sign`/`manage-limits` claims only when actually granted) before assuming
something is broken. This also means requesting scopes speculatively (e.g. always asking for
`openid profile email sign manage-limits` regardless of whether your client is allowlisted for the wallet
scopes yet) is completely safe - you'll just silently not get the ones you're not entitled to, and can add them
to `AllowedScopes` later without changing anything on the client side.

## Wallet API (`sign` / `manage-limits` scopes)

Beyond identity, Biatec OIDC can sign Algorand transaction groups directly on behalf of the wallet owner, subject
to daily/weekly/monthly spending limits the owner controls, in a currency of their choosing. This is a separate,
opt-in capability — request these scopes at `/authorize` only if your integration needs them, and they must be
explicitly added to your client's allowed scopes when it's registered (never granted implicitly, regardless of
what you request).

Full request/response examples, curl snippets, and a live discovery link are on the documentation site at
`https://oidc.biatec.io/` (the `#wallet-api` section) — this section is a summary.

None of these endpoints accept your Google/Microsoft access token as a parameter, ever - only the Biatec bearer
token in the `Authorization` header. The Google/Microsoft token needed behind the scenes to read/write your
self-custody data is resolved entirely from an encrypted copy cached inside that bearer token itself (see
"Provider access token caching" below) - this is what lets the exact same Biatec token work from any
device/backend, not just the one the user originally signed in on.

- **`POST /wallet/sign`** (needs `sign`) — body: `{ "transactions": ["<base64 msgpack>", ...] }`. Every
  payment/asset-transfer in the group is priced in USD via the Biatec Router, and the group's *total* is checked
  against the caller's daily (trailing 24h), weekly (trailing 7d), and monthly (trailing 30d) spending limits
  *before* anything is signed — if the total would exceed any configured (non-zero) limit, the whole request is
  rejected (`403 spending_limit_exceeded`) and nothing is signed. Returns
  `{ "signedTransactions": ["<base64 msgpack>", ...] }` in the same order as the request. A `503`
  (`asset_valuation_failed` or `spending_limit_currency_unavailable`) means a spent asset couldn't be priced, or
  the caller's limit currency's exchange rate couldn't be fetched — every transaction is subject to the limit, so
  an unpriceable asset fails the request rather than being silently treated as free.
- **`GET /wallet/limits`** (only needs to be authenticated, no body/query parameters at all) — read the caller's
  own limits: `{ "currencyCode": "USD", "dailyLimit": 100, "weeklyLimit": 500, "monthlyLimit": 2000 }` (`0` on any
  of the three means that window is unbounded). A first-time caller who's never configured limits gets an
  all-zero, USD-denominated default rather than a 404.
- **`PUT /wallet/limits`** (needs `manage-limits`) — set the caller's own limits: same shape as the `GET`
  response, no other fields. `currencyCode` defaults to `"USD"` if omitted/blank; an unsupported code is rejected
  with `400 unsupported_currency` (see `GET /wallet/limits/currencies` for the supported list). The limits belong
  to the wallet owner, not to your application — they apply the same way across every app the owner has
  authorized with a `sign`-scoped token.
- **`GET /wallet/limits/currencies`** (only needs to be authenticated) — every currency `PUT /wallet/limits`
  will accept, with its current USD rate: `{ "currencies": [ { "code": "USD", "name": null, "usdPerUnit": 1.0 },
  { "code": "EUR", "name": "EMU euro", "usdPerUnit": 1.08 }, ... ] }`. Rates come from the Czech National Bank's
  daily fixing and are cached for several hours - not a real-time feed.
- All three spending-limit files — the settings above and a rolling ledger of every signed payment/asset-transfer
  (used to compute the real trailing spend without re-querying the blockchain on every sign) — are AES-encrypted
  and stored in the wallet owner's own Google Drive/OneDrive folder, exactly like the self-custody account file
  itself. Biatec's servers never persist this data in plaintext.
- The Google/Microsoft token resolved from your bearer token is used once, in-memory, per request, to read and
  decrypt the owner's self-custody file and spending-limit data, and is never persisted by these endpoints
  themselves — same self-custody model as the rest of this service (see the root `CLAUDE.md`). If no provider
  token was ever cached for this session (or it's since gone stale - see below), the call fails with
  `401 storage_access_denied`; there is no parameter to work around this with, the caller needs a fresh
  interactive sign-in through `/authorize`.

## Provider access token caching

The wallet API's whole point is that a relying-party (RP) backend talks to Biatec using only its Biatec-issued
`access_token` — it never sees, stores, or manages the user's actual Google/Microsoft OAuth token itself. That
means Biatec has to be able to get at that provider token on every `/wallet/sign`/`/wallet/limits` call using
*only* the Biatec bearer token the RP presents. This section explains how, and what that trades off.

**The mechanism.** At the moment a Biatec access/refresh token is minted — after the user completes an
interactive Google/Microsoft sign-in, while the ambient cookie session still has their live provider token — that
provider token is AES-256-GCM encrypted (`BusinessLogic/ProviderAccessTokenProtector.cs`, same authenticated
format `AesEncryptionHelper` uses for the self-custody file, but under a **separate, dedicated key** —
`ProviderTokenProtection:Key`/`IV` in config, never `AesOptions`) and embedded as a private claim
(`provider_token`) on the issued Biatec access token. `WalletController` decrypts that claim, in-memory, for the
duration of a single request, on every call - there is no way to supply your own Google/Microsoft token instead;
the Biatec bearer token is the only credential every wallet endpoint accepts. Nothing about this changes what an
RP integrating against this API needs to do: it already just forwards the Biatec `access_token` as a bearer token
everywhere; it never sees, stores, or forwards the user's actual Google/Microsoft token at all.

**Why not a server-side cache (e.g. Redis, keyed by the Biatec token) instead?** That was the alternative
considered here. Embedding the (encrypted) token *inside* the Biatec token the client already holds, rather than
in a lookup table on the server, means there is no new "list of every active user's provider token" for an
attacker to dump in one query if the database/cache is compromised — the ciphertext only exists inside tokens
already scattered across whichever RPs currently hold them, decryptable only with a key that (by design) never
leaves this service's own config/secret store.

**What this does and doesn't protect against.** Being explicit here matters more than usual, because this is
exactly the kind of feature that increases blast radius if Biatec's server is ever compromised — that's the
trade-off being made, deliberately, to support the "RP only ever holds a Biatec token" model:

- If an attacker compromises **only** `ProviderTokenProtection:Key`/`IV` (e.g. a narrow secret-store leak) without
  also compromising `AesOptions` (the self-custody file's key) or `JwtIssuer:SigningPrivateKeyPem`, they can
  decrypt any `provider_token` claim they can get their hands on, but still can't decrypt the self-custody account
  file itself or forge new Biatec tokens. This is why the keys are separate.
- If an attacker gets **full** server compromise (all of the above, plus Redis, plus the ability to intercept live
  traffic), caching or not caching this token changes little — they could already intercept/derive it from live
  requests, or decrypt the self-custody file directly with the leaked `AesOptions` key. The self-custody account
  file (not the provider token) is the actually valuable secret; the provider token by itself only grants
  `drive.file`/`Files.ReadWrite.AppFolder`-scoped access to a single app-created folder, not the account file's
  plaintext mnemonic.
- A **stolen Biatec access token** (e.g. an RP's own backend gets compromised, or a token leaks in a log) now also
  carries a live, usable Google/Microsoft token, for as long as both remain valid. This is no *new* exposure
  specific to caching, though — a stolen Biatec `sign`-scoped access token could already be replayed against
  `/wallet/sign` to move funds up to the spending limit regardless of whether a provider token happened to be
  attached. Treat a leaked access token as fully compromised either way — see "Security Recommendations" below on
  TLS and not logging tokens.
- The cached token **naturally expires** on Google/Microsoft's own schedule (their access tokens typically last
  around an hour) regardless of how long the Biatec access/refresh token chain that carries it stays alive (up to
  `RefreshTokenLifetimeDays`, 30 by default). There is no ambient cookie session at `grant_type=refresh_token` time
  (that's a server-to-server call from the RP, not a browser request) to fetch a *fresher* provider token from, so
  each refresh just carries the same encrypted value forward unchanged. Once the underlying provider token
  actually expires, wallet calls relying on it start failing with `401 storage_access_denied` until the user
  completes a fresh interactive sign-in (a new `/authorize` round trip) — at which point a fresh token is
  captured and cached again. There is no parameter on any wallet endpoint to work around this - a fresh sign-in
  through `/authorize` is the only way to refresh the cached token.
- The key is configured the same way as `AesOptions`/`JwtIssuer:SigningPrivateKeyPem` — via a Kubernetes Secret in
  production (see `k8s/stage/generate-stage-secret.sh` for how stage mints its own, separate copy), never
  committed as a real production secret. If `ProviderTokenProtection:Key`/`IV` is missing or invalid, no
  `provider_token` claim ever gets embedded (nothing throws at issuance time) - but every wallet endpoint would
  then have no way at all to resolve a provider token, so every call fails `401 storage_access_denied` until the
  key is configured. This key is required infrastructure for the wallet API to work at all, unlike before this
  feature existed.

## Configuration

Configure `JwtIssuer` in `appsettings.json`. Leave `Issuer` blank/unset to have it derived from
each incoming request's own scheme+host instead of a fixed value — this is what the production
deployment actually does, specifically so both `oidc.biatec.io` and the legacy `google.biatec.io`
alias each serve their own internally-consistent `iss`/discovery `issuer` without one breaking the
other. Only set `Issuer` explicitly (as below) if this service is reachable at exactly one host.

```json
"JwtIssuer": {
  "Enabled": true,
  "Issuer": "https://oidc.biatec.io",
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
      "DisplayName": "My App",
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

`DisplayName` is optional but recommended — it's the human-friendly name shown to the user on the provider-picker
and consent screens (e.g. "My App") instead of the raw `ClientId` (e.g. "my-app-pkce"). Falls back to `ClientId`
when left blank, so existing clients that never set it keep working, just with a less polished label until it's
filled in.

To let a client use the wallet API, add `"sign"` and/or `"manage-limits"` to its `AllowedScopes` — e.g.
`["openid", "profile", "email", "sign", "manage-limits"]`. Neither is included by default. Requesting one at
`/authorize` without it being allowlisted no longer fails the request outright — see "Scope handling" below —
the scope is just silently dropped, so the caller gets a working login but no `sign`/`manage-limits` claim (and
therefore a `403` if it then tries `/wallet/sign` or `/wallet/limits`). Check the `scope` field the token response
actually returns if you're not sure what was granted.

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

Also configure `ProviderTokenProtection` — the dedicated key that encrypts the cached Google/Microsoft access
token embedded in issued access tokens (see "Provider access token caching" above). **Required** for the wallet
API to work at all (no wallet endpoint accepts a caller-supplied provider token as a fallback) and **deliberately
a separate section from `AesOptions`**, so the two secrets can be rotated independently:

```json
"ProviderTokenProtection": {
  "Key": "<base64, 32 random bytes>",
  "IV": "<base64, 16 random bytes>"
}
```

```bash
openssl rand -base64 32   # Key
openssl rand -base64 16   # IV
```

If left unset (or invalid) outside `Development`, the service refuses to start (same fail-fast precedent as
`JwtIssuer:SigningPrivateKeyPem`) rather than silently leaving every wallet call failing with an unexplained 401.
Rotating this key invalidates every *currently cached* provider token — affected users just need one fresh
interactive sign-in through `/authorize` to re-cache one; it does **not** affect the self-custody account file,
which is encrypted separately under `AesOptions`.

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
GET https://oidc.biatec.io/authorize
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
POST https://oidc.biatec.io/token
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
GET https://oidc.biatec.io/authorize
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
POST https://oidc.biatec.io/token
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
GET https://oidc.biatec.io/connect/endsession
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
  - `VITE_BIATEC_OIDC_END_SESSION_URL=https://oidc.biatec.io/connect/endsession`

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
Implement OpenID Connect authorization code login against issuer https://oidc.biatec.io.
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
