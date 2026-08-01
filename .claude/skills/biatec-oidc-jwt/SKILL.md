---
name: biatec-oidc-jwt
description: Reference for this repo's OIDC/JWT identity provider (JwtIssuerService, JwtIssuerController, RedirectUriMatcher) and its wallet API (WalletController, ISpendingLimitService, IAssetValuationService, IExchangeRateService, IProviderAccessTokenProtector, AlgorandTransactionInspector) — endpoints, claims/scopes, redirect-URI/logout allowlist rules, signing-key format, daily/weekly/monthly spending-limit enforcement via the Biatec Router + Czech National Bank FX rates, and the encrypted provider-access-token caching embedded in issued tokens. Load this before changing anything under /authorize, /token, /userinfo, /introspect, /verify, /connect/endsession, /logout, /wallet/sign, /wallet/limits, /wallet/limits/currencies, JwtIssuerService.cs, JwtIssuerController.cs, WalletController.cs, WalletService.cs, SpendingLimitService.cs, ProviderAccessTokenProtector.cs, BiatecRouterValuationService.cs, CnbExchangeRateService.cs, AlgorandTransactionInspector.cs, RedirectUriMatcher.cs, or JwtIssuer:*/SpendingLimits:*/ExchangeRates:*/ProviderTokenProtection:* config, instead of re-reading OIDC_INTEGRATION_GUIDE.md and BIATEC_OIDC_LOGOUT_REQUIREMENTS.md in full.
---

# Biatec OIDC / JWT issuer

All source referenced below lives in the `BiatecOIDC/` project (a separate deployment from `BiatecMCP` — see
[[../../../CLAUDE.md]] for the split). Condensed from `BiatecOIDC/OIDC_INTEGRATION_GUIDE.md` and
`BiatecOIDC/BIATEC_OIDC_LOGOUT_REQUIREMENTS.md`. Read those files directly only if you need exact wording for an
external integration doc — for implementation work, this file plus the source is enough.

## Endpoints (JwtIssuerController)

- `GET /.well-known/openid-configuration` — discovery metadata (includes `end_session_endpoint`,
  `frontchannel_logout_supported: false`, `backchannel_logout_supported: false`)
- `GET /.well-known/jwks.json` — public signing keys
- `GET /authorize` — standard `response_type=code` (exchange at `/token`), plus a legacy `returnUrl` alias that
  POSTs `id_token` directly to the return URL. Accepts PKCE `code_challenge`/`code_challenge_method` (RFC 7636).
  Accepts `idp=google|microsoft` to skip the provider picker (the "fast track"); omitting it redirects to
  `/select-provider` first. `/authorize/challenge` issues the actual provider `Challenge`; `/authorize/callback`
  resumes after sign-in, verifies storage-write access via `catalog.Resolve(idp).HasWriteAccessAsync(...)`
  (retrying once with forced consent if missing), then finalizes.
- `POST /token` — authorization code exchange (accepts PKCE `code_verifier`) and refresh-token renewal
- `GET /userinfo` — claims from access token
- `POST /introspect`, `POST /verify` — token activity/verification
- `GET /connect/endsession` (alias `GET /logout`) — RP-Initiated Logout 1.0

## Claims issued

`email`, `algorand_address` (**optional** — omitted if the user denied Drive/OneDrive consent; treat as optional,
request storage scope only right before storage-backed operations; resolved from Google Drive or OneDrive
depending on the signed-in principal's `biatec_idp` claim, see `AuthSchemeNames`), `preferred_username`/`name`
(first 4 + last 4 chars of the Algorand address), plus standard `sub`, `iss`, `aud`, `exp`, `iat`, `nbf`, `jti`.

Access tokens (not ID tokens) additionally carry, when applicable:
- `biatec_idp` — which provider (`Google`/`Microsoft`) the wallet is stored under, same value as the cookie
  session's `AuthSchemeNames.IdpClaimType` claim, captured onto the `AuthorizationCodeRecord`/`RefreshTokenRecord`
  at issuance so it survives the code exchange and refresh flows. `WalletController` uses this to resolve the
  right `ICloudStorageProvider` for `/wallet/sign` - it is never caller-supplied, so it can't be spoofed.
- `sign` (value `"true"`) — present only if the `sign` scope was requested **and** allowlisted for the client.
  Required by `WalletController.SignTransactionGroup` (`POST /wallet/sign`).
- `manage-limits` (value `"true"`) — present only if the `manage-limits` scope was requested and allowlisted.
  Required by `WalletController.UpdateSpendingLimit` (`PUT /wallet/limits`) only - `GetSpendingLimit`
  (`GET /wallet/limits`) and `GetSupportedCurrencies` (`GET /wallet/limits/currencies`) only require a validly
  authenticated caller (the standard `openid` scope), no `manage-limits` needed to read.
- `provider_token` — the caller's Google/Microsoft access token, AES-256-GCM encrypted under a key dedicated to
  this (`ProviderTokenProtectionConfiguration`, never `AesOptions`) - see "Provider access token caching" below.

These two are deliberately explicit claims, not something callers infer by re-parsing the space-separated `scope`
claim — see `JwtIssuerService.CreateAccessToken`'s `WalletApiScopes` array. Neither is in any client's
`AllowedScopes` by default (`{"openid","profile","email"}`) - adding wallet capability to a client is an explicit
allowlist edit in `JwtIssuer:Clients`, never implicit.

## Wallet API (WalletController, `/wallet/*`)

Not part of the OIDC protocol itself - a Biatec-specific self-custody API layered on top, authenticated the same
way as `/userinfo`/`/introspect` (manual `Authorization: Bearer` extraction + `ValidateBearerAccessToken`, **not**
`[Authorize]`/a JWT Bearer scheme - see "Why manual token parsing" below).

- `POST /wallet/sign` — requires the `sign` claim. Body: `{ "transactions": ["<base64 msgpack>", ...],
  "accessToken": "<optional provider access token>" }`. Every `pay`/`axfer` transaction in the group is priced in
  USD via `IAssetValuationService` (`BiatecRouterValuationService`, quoting against the Biatec Router - see
  below), summed, and the total is checked against the caller's daily (trailing 24h)/weekly (trailing
  7d)/monthly (trailing 30d) spending limits (`ISpendingLimitService.EnsureWithinLimitsAsync`) *before* any
  transaction is signed via the shared `IDriveService.SignTransactionAsync` - a group that would exceed a limit
  never partially signs. Signed spend is then recorded to the caller's encrypted ledger
  (`ISpendingLimitService.RecordSpendAsync`). `accessToken` is the Google/Microsoft token needed to read/decrypt
  the self-custody file *and* the spending-limit data (never persisted server-side in plaintext) - optional,
  since `WalletController.ResolveProviderAccessToken` falls back to decrypting the bearer token's own
  `provider_token` claim when omitted (see "Provider access token caching" below); an explicit value always
  wins if supplied. Throws (mapped to HTTP by `WalletController`):
  `SpendingLimitExceededException` → 403, `FormatException` (bad transaction) → 400, `UnauthorizedAccessException`
  (expired/invalid provider token) → 401, `AssetValuationException`/`UnsupportedCurrencyException` → 503 (a spent
  asset couldn't be priced, or the limit currency's FX rate couldn't be fetched - every transaction is subject to
  the limit, so this fails closed rather than treating an unpriceable asset as free).
- `GET /wallet/limits` — only requires a valid bearer token (any authenticated caller reads their own limits, no
  `manage-limits` claim). `PUT /wallet/limits` requires the `manage-limits` claim. Limits (`SpendingLimitSettings`:
  `CurrencyCode` + `DailyLimit`/`WeeklyLimit`/`MonthlyLimit`, `0` = unbounded per window) and the signed-transaction
  ledger (`SpendingLedgerEntry` list, USD-denominated, pruned to the last 30 days on every write) are **not** in
  Redis - `ISpendingLimitService`/`SpendingLimitService` stores both AES-encrypted in the wallet owner's own
  cloud drive (same `ICloudStorageProviderCatalog`/`AesEncryptionHelper` primitives `CloudAccountRepository` uses
  for the account file itself), under `SpendingLimits.<AESID>.dat`/`SpendingLedger.<AESID>.dat`. Windows are
  rolling (measured back from "now"), not calendar-aligned - deliberately, so a limit can't be doubled up by
  spending right before and right after a calendar boundary. Global per user (by email), not per relying-party
  client - any app holding a `sign`-scoped token for that user is bound by the same limits, wherever last set.
- `GET /wallet/limits/currencies` — only requires a valid bearer token. Lists every currency `PUT /wallet/limits`
  accepts plus its current USD rate, via `IExchangeRateService`/`CnbExchangeRateService` (Czech National Bank's
  daily fixing JSON API, cached in `IDistributedCache`/Redis for `ExchangeRateConfiguration.CacheDurationMinutes`,
  default 6h). USD is always supported (rate `1.0`, no fetch needed); CZK is added locally since ČNB never quotes
  CZK against itself; every other code comes from the fixing table, converted via CZK as the pivot currency
  (`usdPerUnit(C) = czkPerUnit(C) / czkPerUnit(USD)`).
- `IAssetValuationService`/`BiatecRouterValuationService` prices a spent asset by quoting it against
  `SpendingLimitsConfiguration.UsdReferenceAssetId` (mainnet USDC, `31566704`, by default) via the
  `BiatecRouterConnector` NuGet package's public, unauthenticated `/quote` endpoint (`IBiatecRouterQuoteClient` -
  a thin seam around the generated client, for testability). Spending the reference asset itself converts locally
  (no router call needed). `EnsureWithinLimitsAsync` then converts that USD figure into the caller's configured
  limit currency via `IExchangeRateService.ConvertFromUsdAsync` before comparing against the configured ceiling.

`AlgorandTransactionInspector` (`BiatecOIDC/Helper/AlgorandTransactionInspector.cs`) decodes a transaction's raw
msgpack to find its real type, amount, and asset id. This needs a generic (untyped) msgpack map decode first - the
Algorand4 SDK's `Transaction` subclasses' `type` property is a hardcoded C# constant of that subclass, **not**
something decoded off the wire (verified empirically: decoding a payment's bytes as `AssetTransferTransaction`
silently reports `type="axfer"`). Handles both a bare `Transaction` and a `SignedTransaction` wrapper (multisig
co-signing - the real fields are nested one level down, under the wire key `"txn"`). Anything that isn't
`pay`/`axfer` (app calls, asset config, key registration, ...) returns `AlgorandTransactionKind.Other` and is not
spending-limit-checked, per the current scope of this feature.

## Provider access token caching

`ProviderAccessTokenProtector`/`IProviderAccessTokenProtector` (`BiatecOIDC/BusinessLogic/`) AES-256-GCM encrypts
the caller's Google/Microsoft access token under a **dedicated** key (`ProviderTokenProtectionConfiguration`,
bound from `ProviderTokenProtection:Key`/`IV` - never `AesOptions`, so the two secrets rotate independently) and
embeds it as the `provider_token` claim on issued access tokens, so wallet API callers don't have to separately
manage/resend their own provider token. Reuses `AesEncryptionHelper`'s exact authenticated format, bound to the
caller's email (so a ciphertext for one user can never decrypt under another's).

- **Captured** in `JwtIssuerController.FinalizeAuthorizeAsync` via `provider.GetAmbientAccessTokenAsync()`
  (deliberately not the plain `HttpContext.GetTokenAsync` used elsewhere in that controller - Google's
  implementation proactively refreshes a near-expired token, maximizing how long the cached copy stays valid)
  and passed into `JwtIssuerService.CreateAuthorizeResponseAsync`'s new `providerAccessToken` parameter. Encrypted
  there and stored on `AuthorizationCodeRecord.ProtectedProviderAccessToken` (Redis, `oidc:code:` prefix).
- **Propagated** through `ExchangeTokenAsync`'s `authorization_code` grant into the issued access token's
  `provider_token` claim (via `CreateAccessToken`) and into the new `RefreshTokenRecord` (`oidc:refresh:` prefix),
  so it survives the code exchange (which has no ambient cookie session of its own - the RP's backend calls
  `/token` server-to-server).
- **Carried forward unchanged** on `grant_type=refresh_token` - there's no ambient session at refresh time to
  source a fresher provider token from, so the same encrypted value just rotates into the new refresh record.
  Once the underlying Google/Microsoft token naturally expires (their own ~1h lifetime, regardless of how long
  the Biatec refresh-token chain survives, up to `RefreshTokenLifetimeDays`), wallet calls relying on it start
  failing `401 storage_access_denied` until the user does a fresh interactive `/authorize` sign-in.
- **Consumed** by `WalletController.ResolveProviderAccessToken`: an explicit caller-supplied `accessToken` always
  wins; otherwise it decrypts the bearer token's `provider_token` claim (bound to the same email) in-memory, for
  that one request only.
- **Fails safe, never loud**: if `ProviderTokenProtection:Key`/`IV` is missing/invalid,
  `ProviderAccessTokenProtector.Protect`/`Unprotect` return `null` (never throw) - no claim gets embedded, and
  every wallet endpoint keeps working exactly as before this existed, just requiring an explicit `accessToken`.
- **Threat model** (full writeup in `OIDC_INTEGRATION_GUIDE.md`'s "Provider access token caching" section): the
  dedicated key compartmentalizes this from `AesOptions` (the self-custody file's key) and
  `JwtIssuer:SigningPrivateKeyPem`, so a leak of just this one doesn't also compromise the mnemonic file or let an
  attacker forge tokens. Embedding in the client-held token (vs. a server-side lookup table keyed by user) means
  there's no single "dump every active user's provider token" query surface if a datastore is compromised.

### Why manual token parsing, not `[Authorize]`

Same reasoning as `/userinfo`/`/introspect`/`/verify` in `JwtIssuerController`: the default challenge scheme is
Google OIDC (browser redirect), so a declarative `[Authorize]` would redirect an API caller instead of returning
401/403 JSON. `WalletController` mirrors the existing pattern (`[AllowAnonymous]` + `BearerTokenHelper.ExtractBearerToken`
+ `IJwtIssuerService.ValidateBearerAccessToken`) rather than introducing a second, parallel `AddJwtBearer` scheme
that would have to reimplement the same dynamic per-client audience validation `ValidateBearerAccessToken`
already does.

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
