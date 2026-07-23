---
name: biatec-oidc-jwt
description: Reference for this repo's OIDC/JWT identity provider (JwtIssuerService, JwtIssuerController, RedirectUriMatcher) — endpoints, claims, redirect-URI/logout allowlist rules, signing-key format. Load this before changing anything under /authorize, /token, /userinfo, /introspect, /verify, /connect/endsession, /logout, JwtIssuerService.cs, JwtIssuerController.cs, RedirectUriMatcher.cs, or JwtIssuer:* config, instead of re-reading OIDC_INTEGRATION_GUIDE.md and BIATEC_OIDC_LOGOUT_REQUIREMENTS.md in full.
---

# Biatec OIDC / JWT issuer

Condensed from `AlgorandGoogleDriveAccount/OIDC_INTEGRATION_GUIDE.md` and
`AlgorandGoogleDriveAccount/BIATEC_OIDC_LOGOUT_REQUIREMENTS.md`. Read those files directly only if you need
exact wording for an external integration doc — for implementation work, this file plus the source is enough.

## Endpoints (JwtIssuerController)

- `GET /.well-known/openid-configuration` — discovery metadata (includes `end_session_endpoint`,
  `frontchannel_logout_supported: false`, `backchannel_logout_supported: false`)
- `GET /.well-known/jwks.json` — public signing keys
- `GET /authorize` — standard `response_type=code` (exchange at `/token`), plus a legacy `returnUrl` alias that
  POSTs `id_token` directly to the return URL. Accepts PKCE `code_challenge`/`code_challenge_method` (RFC 7636).
- `POST /token` — authorization code exchange (accepts PKCE `code_verifier`) and refresh-token renewal
- `GET /userinfo` — claims from access token
- `POST /introspect`, `POST /verify` — token activity/verification
- `GET /connect/endsession` (alias `GET /logout`) — RP-Initiated Logout 1.0

## Claims issued

`email`, `algorand_address` (**optional** — omitted if the user denied Google Drive consent; treat as optional,
request Drive scope only right before Drive-backed operations), `preferred_username`/`name` (first 4 + last 4
chars of the Algorand address), plus standard `sub`, `iss`, `aud`, `exp`, `iat`, `nbf`, `jti`.

## Client registration (`JwtIssuer:Clients` in appsettings.json)

Each client has `RedirectUris` and `PostLogoutRedirectUris`. Matching rules (`Helper/RedirectUriMatcher.cs`):

- Must be an absolute URI; matched on scheme + host + port + path.
- `*` wildcards allowed for subdomains/variable segments, e.g. `https://*.example.com/login` matches
  `https://tenant-a.example.com/login` but **not** `https://example.com/login` — register the root domain
  separately if it's also needed.
- Query parameters are allowed at runtime as long as the base URI (without query) is allowlisted, e.g. allowlisted
  `http://localhost:5173/login` accepts `http://localhost:5173/login?redirect=%2F&oidc_retry=consent`.
- If `PostLogoutRedirectUris` is empty for a client, logout redirect falls back to that client's `RedirectUris`.
- Redirect URI matching must stay a strict allowlist — never loosen to permissive/prefix matching without
  explicit instruction (see [[../../../CLAUDE.md]] conventions section).
- `RedirectUris` may use non-`http(s)` custom schemes (e.g. `io.example.myapp:/oauth2redirect` for a native
  Android/iOS app) — `RedirectUriMatcher` only checks scheme/host/port/path/query, it doesn't require http(s).

## PKCE (RFC 7636) — public clients (mobile/desktop/SPA)

A client is "public" whenever `JwtIssuerClientConfiguration.ClientSecret` is null/empty
(`JwtIssuerClientConfiguration.IsPublicClient`). Public clients cannot hold a confidential secret, so PKCE replaces
`client_secret` as the authorization-code-theft defense:

- `ValidateAuthorizeRequestAsync` (`JwtIssuerService.cs`) rejects `response_type=code` with `invalid_request` if the
  resolved client `IsPublicClient` and `code_challenge` is missing. `code_challenge_method` must be `S256` or
  `plain` (defaults to `plain` if omitted); `code_challenge` must be 43–128 chars.
- The challenge is stored on the `AuthorizationCodeRecord` alongside the issued `code` (Redis, `oidc:code:` prefix).
- `ExchangeTokenAsync` validates `code_verifier` against the stored challenge via the private `ValidatePkce` helper
  (`S256` = base64url(SHA256(verifier)) must equal challenge; `plain` = verifier must equal challenge byte-for-byte)
  before honoring `grant_type=authorization_code`. Mismatch/missing verifier → `invalid_grant`.
- Confidential clients (have a `ClientSecret`) may still send PKCE — it's validated if present but not required;
  it does not replace the secret check in `ValidateClientAuthentication`.
- Refresh (`grant_type=refresh_token`) never requires `code_verifier` — PKCE only guards the code exchange step.

## Logout endpoint parameters

`id_token_hint`, `post_logout_redirect_uri`, `state`, `client_id` — all recommended, not required. If
`post_logout_redirect_uri` is given, it must resolve to a known client via `client_id` or the `aud` in
`id_token_hint`.

## Signing keys

RS256 only (current `Microsoft.IdentityModel.Tokens` package doesn't expose EdDSA primitives). Accepts PKCS#8
(`-----BEGIN PRIVATE KEY-----`) or PKCS#1 (`-----BEGIN RSA PRIVATE KEY-----`) PEM — **not** OpenSSH format (i.e.
not the default output of `ssh-keygen`). Generate with:

```bash
openssl genpkey -algorithm RSA -pkeyopt rsa_keygen_bits:4096 -out jwt-signing-private.pem
```

`JwtIssuer:SigningPrivateKeyPem` accepts either inline PEM (escaped `\n`) or a file path that the service resolves
and reads.

## Non-goals (current scope)

Token revocation endpoint usage by frontend SPAs, and back-channel logout to relying parties — do not assume
these exist.
