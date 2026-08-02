---
name: biatec-oidc-jwt
description: Reference for this repo's OIDC/JWT identity provider (JwtIssuerService, JwtIssuerController, RedirectUriMatcher) and its wallet API (WalletController, ISpendingLimitService, IAssetValuationService, IExchangeRateService, IProviderAccessTokenProtector, AlgorandTransactionInspector, ICloudAccountRepository's multi-seed vault, IVaultBackupService) — endpoints, claims/scopes (including the two-tier scope handling - a recognized-but-non-allowlisted scope like `manage-limits` hard-fails with invalid_scope naming it, while an unrecognized scope like a literal ".default" is silently dropped, and the strict `rekey` scope required for any rekey transaction), redirect-URI/logout allowlist rules, signing-key format, daily/weekly/monthly spending-limit enforcement via the Biatec Router + Czech National Bank FX rates, the encrypted provider-access-token caching embedded in issued tokens, including its automatic renewal from
a cached provider refresh token (both on Biatec token refresh and, opportunistically, mid-request in
WalletController), the multi-seed vault (GET/POST /wallet/seeds, PUT /wallet/seeds/primary), and explicit
cross-cloud vault backup (POST/GET /wallet/backup/*). Load this before changing anything under /authorize, /token, /userinfo, /introspect, /verify, /connect/endsession, /logout, /wallet/sign, /wallet/limits, /wallet/limits/currencies, /wallet/seeds, /wallet/backup, JwtIssuerService.cs, JwtIssuerController.cs, WalletController.cs, WalletService.cs, SpendingLimitService.cs, ProviderAccessTokenProtector.cs, BiatecRouterValuationService.cs, CnbExchangeRateService.cs, AlgorandTransactionInspector.cs, RedirectUriMatcher.cs, CloudAccountRepository.cs, VaultBackupService.cs, VaultBackupController.cs, or JwtIssuer:*/SpendingLimits:*/ExchangeRates:*/ProviderTokenProtection:* config, instead of re-reading OIDC_INTEGRATION_GUIDE.md and BIATEC_OIDC_LOGOUT_REQUIREMENTS.md in full.
---

# Biatec OIDC / JWT issuer

All source referenced below lives in the `BiatecOIDC/` project (a separate deployment from `BiatecMCP` — see
[[../../../CLAUDE.md]] for the split). Condensed from `BiatecOIDC/OIDC_INTEGRATION_GUIDE.md` and
`BiatecOIDC/BIATEC_OIDC_LOGOUT_REQUIREMENTS.md`. Read those files directly only if you need exact wording for an
external integration doc — for implementation work, this file plus the source is enough.

## Endpoints (JwtIssuerController)

- `GET /.well-known/openid-configuration` — discovery metadata (includes `end_session_endpoint`,
  `frontchannel_logout_supported: false`, `backchannel_logout_supported: false`)
- `GET /.well-known/oauth-authorization-server` — RFC 8414 metadata, identical document to the OIDC
  discovery endpoint above; served for OAuth/MCP clients that probe this URL before falling back to OIDC
  discovery
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
- `rekey` (value `"true"`) — present only if the `rekey` scope was requested and allowlisted. The strictest
  wallet claim: required by `SignTransactionGroup` (`POST /wallet/sign`) whenever the transaction group
  contains a transaction with Algorand's `rekey` field set (`AlgorandTransactionInspector`'s `IsRekey`), in
  *addition* to `sign` - a `sign`-only token still gets 403 for a rekey transaction. Also required by
  `CreateSeed` (`POST /wallet/seeds`), since minting a spare seed is the first step of the recovery-from-
  compromise flow this scope exists for. See "Multi-seed vault" below.
- `provider_token` — the caller's Google/Microsoft access token, AES-256-GCM encrypted under a key dedicated to
  this (`ProviderTokenProtectionConfiguration`, never `AesOptions`) - see "Provider access token caching" below.
- `provider_refresh_token` — the caller's Google/Microsoft refresh token, encrypted the same way
  (`ProviderAccessTokenProtector.RefreshClaimType`), used to renew `provider_token` once it expires without
  requiring a fresh interactive sign-in every time - see "Provider access token caching" below.

These two are deliberately explicit claims, not something callers infer by re-parsing the space-separated `scope`
claim — see `JwtIssuerService.CreateAccessToken`'s `WalletApiScopes` array. Neither is in any client's
`AllowedScopes` by default (`{"openid","profile","email"}`) - adding wallet capability to a client is an explicit
allowlist edit in `JwtIssuer:Clients`, never implicit.

## Scope handling (`ValidateAuthorizeRequestAsync`)

Two different failure modes for two different kinds of "unexpected" requested scope - deliberately not the same:

- **Recognized but not allowlisted** - `sign`/`manage-limits` (`WalletApiScopes`) requested by a client whose
  `AllowedScopes` doesn't include them: **hard-fails** the whole `/authorize` request with `invalid_scope`, error
  description naming exactly which scope(s) (e.g. `"This client is not allowlisted for scope(s): manage-limits.
  ..."`). Deliberately loud - a developer who explicitly requested `manage-limits` and expected to get it should
  see a clear error, not silently receive a token with no `manage-limits` claim and have to guess why.
- **Unrecognized entirely** - a typo, or a scope some OIDC client library auto-appends regardless of what you
  configured (e.g. MSAL/Azure AD-flavored clients sending a literal `.default`): **silently dropped**, never
  rejected - there's nothing to fix, and failing login over library-injected noise would be worse.

`openid` is always required (`invalid_scope` if missing) and `profile`/`email` (`AlwaysGrantedScopes`) are always
granted regardless of `AllowedScopes` - only the wallet scopes are actually gated by it. The grant that survives
is written back onto `normalized.Scope` and is what the token response's `scope` field reflects.

## Wallet API (WalletController, `/wallet/*`)

Not part of the OIDC protocol itself - a Biatec-specific self-custody API layered on top, authenticated the same
way as `/userinfo`/`/introspect` (manual `Authorization: Bearer` extraction + `ValidateBearerAccessToken`, **not**
`[Authorize]`/a JWT Bearer scheme - see "Why manual token parsing" below).

- `POST /wallet/sign` — requires the `sign` claim; if any transaction in the group has Algorand's `rekey` field
  set (`AlgorandTransactionInspector.Inspect(...).IsRekey`, checked in `WalletController.SignTransactionGroup`
  right after decoding, before anything else), also requires the `rekey` claim - a `sign`-only token gets 403
  `insufficient_scope` naming `rekey`, and nothing in the group is signed. Body: `{ "transactions": ["<base64 msgpack>", ...] }` - no
  provider-token field, no wallet endpoint accepts one; the Google/Microsoft token needed to read/decrypt the
  self-custody file *and* the spending-limit data is always resolved from the bearer token's own encrypted
  `provider_token` claim, via `WalletController.ResolveProviderAccessToken` (see "Provider access token caching"
  below) - never persisted server-side in plaintext, never a caller-supplied parameter. Every `pay`/`axfer`
  transaction in the group is priced in USD via `IAssetValuationService` (`BiatecRouterValuationService`, quoting
  against the Biatec Router - see below), summed, and the total is checked against the caller's daily (trailing
  24h)/weekly (trailing 7d)/monthly (trailing 30d) spending limits (`ISpendingLimitService.EnsureWithinLimitsAsync`)
  *before* any transaction is signed via the shared `IDriveService.SignTransactionAsync` - a group that would
  exceed a limit never partially signs. Signed spend is then recorded to the caller's encrypted ledger
  (`ISpendingLimitService.RecordSpendAsync`). Throws (mapped to HTTP by `WalletController`):
  `SpendingLimitExceededException` → 403, `FormatException` (bad transaction) → 400, `UnauthorizedAccessException`
  (no provider token was ever cached, or it's since gone stale/expired) → 401,
  `AssetValuationException`/`UnsupportedCurrencyException` → 503 (a spent asset couldn't be priced, or the limit
  currency's FX rate couldn't be fetched - every transaction is subject to the limit, so this fails closed rather
  than treating an unpriceable asset as free).
- `GET /wallet/limits` — only requires a valid bearer token, no other parameters at all (any authenticated caller
  reads their own limits, no `manage-limits` claim). `PUT /wallet/limits` requires the `manage-limits` claim, no
  other parameters beyond the limit values themselves. Limits (`SpendingLimitSettings`:
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
msgpack to find its real type, amount, and asset id, and separately whether it's a rekey. This needs a generic
(untyped) msgpack map decode first - the Algorand4 SDK's `Transaction` subclasses' `type` property is a
hardcoded C# constant of that subclass, **not** something decoded off the wire (verified empirically: decoding
a payment's bytes as `AssetTransferTransaction` silently reports `type="axfer"`). Handles both a bare
`Transaction` and a `SignedTransaction` wrapper (multisig co-signing - the real fields are nested one level
down, under the wire key `"txn"`). Anything that isn't `pay`/`axfer` (app calls, asset config, key
registration, ...) returns `AlgorandTransactionKind.Other` and is not spending-limit-checked, per the current
scope of that feature - `IsRekey` is read independently of `Kind`, since a rekey can accompany any transaction
type (wire key `"rekey"`, a 32-byte address; present whenever non-empty).

## Multi-seed vault (`ICloudAccountRepository`, `BiatecSelfCustodyCore/Repository/`)

The account file's decrypted content is a `SeedVault` (`BiatecSelfCustodyCore.Model`) - `List<SeedVaultEntry>`,
each entry `{ Mnemonic, PrimaryAddress (its own ARC-76 slot-0 address, used as the entry's identifier instead
of a separate id), CreatedUtc, IsPrimary }`. Exactly one entry is `IsPrimary` at a time;
`LoadAccountAsync(email, slot, provider, accessToken)` **keeps its pre-existing signature** and always derives
from whichever seed is primary via `ARC76.GetEmailAccount(email, primary.Mnemonic, slot)` - `slot` still
parameterizes derivation *within* that seed exactly as before this existed, so `BiatecMCP`'s `getAlgorandAddress`
and everything else calling `LoadAccountAsync` needed zero changes.

- `CloudAccountRepository.LoadVaultOrEmptyAsync`/`LoadVaultEnsuringAtLeastOneSeedAsync` - the former never
  side-effect-creates a seed (used by `ListSeedsAsync`/`CreateSeedAsync`/`SwitchPrimarySeedAsync`, which need to
  see a genuinely-empty vault as empty to make correct decisions); the latter auto-creates the first seed if
  none exists yet (used by `LoadAccountAsync`/`GetEncryptedVaultForBackupAsync`, which need something to
  work with). Getting this distinction wrong was a real bug caught by
  `CloudAccountRepositoryTests.CreateSeedAsync_AsTheVeryFirstSeed_IsAutomaticallyPrimary` during development -
  `CreateSeedAsync` must NOT go through the auto-create path, or a truly-first `CreateSeedAsync` call ends up
  minting *two* seeds (the auto-created one plus its own) and returns the wrong one as primary.
- An existing plain-mnemonic file (from before this feature existed) is transparently wrapped into a
  single-seed vault (`IsPrimary = true`) the first time it's read and re-saved immediately - same "migrate on
  read" self-healing philosophy as `EncryptedKeyRingFileStore`'s AES key-ring migration, and composes with it:
  a file can need *both* migrations (legacy format *and* a historical AES key) in one pass.
- `WalletController` exposes this as `GET /wallet/seeds` (list, `openid` only, never returns a mnemonic),
  `POST /wallet/seeds` (mint a new seed, requires `rekey` - the new seed starts non-primary unless it's the
  very first seed ever), and `PUT /wallet/seeds/primary` (switch primary, requires `sign`, 400 if the given
  address isn't in the vault). Biatec never builds/submits the on-chain rekey transaction itself - see
  `OIDC_INTEGRATION_GUIDE.md`'s "Multi-seed vault and rekey" section for the full recovery-flow sequence
  (mint seed → RP builds+submits a `rekey`-claim-gated `/wallet/sign` call → wait for confirmation → only then
  switch primary).
- `GetEncryptedVaultForBackupAsync(email, provider, accessToken)` returns the vault's current file name and
  raw (still-encrypted) bytes, ensuring it's migrated onto the active AES key/active-generation file name
  first - used only by `VaultBackupService` below, never decrypts anything itself.

## Cross-cloud vault backup (`IVaultBackupService`/`VaultBackupService`, `VaultBackupController`)

Explicit, user-triggered copy of the encrypted vault file to a second cloud provider - mitigates losing every
key to a single provider ban/forgotten credentials. Modeled on `BiatecMCP`'s `DevicePairingService` Redis
session/poll pattern but implemented fresh in `BiatecOIDC` (separately deployed, no shared runtime state), and
deliberately using a **manual OAuth2 authorization-code exchange** - not `Challenge()`/`AddOpenIdConnect` -
specifically because `Challenge()`-ing a second provider scheme would re-fire that scheme's `OnTokenValidated`
(`CloudStorageProviderClaims.Stamp`) against the caller's *real* cookie session, silently overwriting
`biatec_idp`. Two new `ICloudStorageProvider` members carry each provider's own OAuth specifics:
`BuildAuthorizationUrl(redirectUri, state)` (that provider's `/authorize` endpoint + its `RequiredScope`, only)
and `ExchangeAuthorizationCodeAsync(code, redirectUri)` (`grant_type=authorization_code` against the same
token endpoint `RefreshAccessTokenAsync` already POSTs to for each provider).

- Redis records (`IConnectionMultiplexer`, same style as `JwtIssuerService`'s `AuthorizationCodeRecord`):
  `vaultbackup:pending:{linkId}` (`PendingVaultBackup{Email, TargetProvider}`, ~15 min TTL, written by
  `StartAsync`) and `vaultbackup:linked:{linkId}` (adds the target provider's raw access token, ~10 min TTL,
  one-shot read-and-delete via `StringGetDeleteAsync` in `CompleteAsync` - never usable twice, never lingers).
- `POST /wallet/backup/start` (needs `sign`) → `StartAsync` (throws if `targetProvider` equals the caller's
  current provider, or isn't a recognized+configured one) → `{ linkId, authorizeUrl }`.
- `GET /wallet/backup/authorize?linkId=...` (`[AllowAnonymous]`, browser) → looks up the pending record,
  302s to `provider.BuildAuthorizationUrl(callbackUrl, linkId)`.
- `GET /wallet/backup/callback?code&state` (`[AllowAnonymous]`, browser, `state` = `linkId`) →
  `HandleCallbackAsync`: exchanges the code, verifies `HasWriteAccessAsync`, writes the linked record, deletes
  the pending one, renders a plain confirmation page.
- `POST /wallet/backup/complete` (needs `sign`) → `CompleteAsync`: reads-and-deletes the linked record
  (fails if missing/expired, or its `Email` doesn't match the caller), calls
  `ICloudAccountRepository.GetEncryptedVaultForBackupAsync` against the caller's **primary** provider (using
  the bearer token's own cached `provider_token`, same resolution as everywhere else in `WalletController`),
  and `UploadAsync`s the identical bytes to the target provider under the same file name - no re-encryption,
  since both live under the same `AesOptions` key ring regardless of storage backend.

## Provider access token caching

`ProviderAccessTokenProtector`/`IProviderAccessTokenProtector` (`BiatecOIDC/BusinessLogic/`) AES-256-GCM encrypts
the caller's Google/Microsoft access token under a **dedicated, independently rotatable key ring**
(`ProviderTokenProtectionConfiguration : IAesKeyRingConfiguration` - an `ActiveKeyId` plus a `Keys[]` list of
`{KeyId, Key, IV}` generations, bound from `ProviderTokenProtection` - never `AesOptions`, so the two rings
rotate independently) and embeds it as the `provider_token` claim on issued access tokens, so wallet API callers
don't have to separately manage/resend their own provider token. Reuses `AesEncryptionHelper`'s exact
authenticated format, bound to the caller's email (so a ciphertext for one user can never decrypt under
another's). `Protect` always uses the active key (`AesKeyRingResolver.GetActiveKey`); `Unprotect` tries the
active key then every historical key in turn (`AesKeyRingResolver.GetHistoricalKeys`) - safe blind trial-decrypt
because this protector only ever writes the authenticated format, so a wrong key deterministically fails the
GCM auth-tag check. Rotating just means adding a new `Keys[]` entry and flipping `ActiveKeyId` - every
newly-issued/refreshed token picks up the new active key automatically (see `JwtIssuerService.CreateAccessToken`/
`RenewProviderTokenAsync`), and already-cached tokens keep decrypting as long as the key that encrypted them is
still in `Keys[]`. See `OIDC_INTEGRATION_GUIDE.md`'s "Key rotation" section for the full runbook.

- **Captured** in `JwtIssuerController.FinalizeAuthorizeAsync` via `provider.GetAmbientAccessTokenAsync()`
  (deliberately not the plain `HttpContext.GetTokenAsync` used elsewhere in that controller - Google's
  implementation proactively refreshes a near-expired token, maximizing how long the cached copy stays valid) and
  `provider.GetAmbientRefreshTokenAsync()`, passed into `JwtIssuerService.CreateAuthorizeResponseAsync`'s
  `providerAccessToken`/`providerRefreshToken` parameters. Both encrypted there and stored on
  `AuthorizationCodeRecord.ProtectedProviderAccessToken`/`ProtectedProviderRefreshToken` (Redis, `oidc:code:`
  prefix). Google's `OnRedirectToIdentityProvider` (`Program.cs`) sends `access_type=offline` so Google actually
  issues a refresh token to capture (safe to always send - unlike `prompt=consent`, it doesn't force a re-consent
  screen every sign-in).
- **Propagated** through `ExchangeTokenAsync`'s `authorization_code` grant into the issued access token's
  `provider_token`/`provider_refresh_token` claims (via `CreateAccessToken`) and into the new `RefreshTokenRecord`
  (`oidc:refresh:` prefix), so both survive the code exchange (which has no ambient cookie session of its own -
  the RP's backend calls `/token` server-to-server).
- **Renewed** on `grant_type=refresh_token` by `JwtIssuerService.RenewProviderTokenAsync`: there's no ambient
  cookie session at refresh time, but if a `provider_refresh_token` was cached, it's decrypted and spent via
  `ICloudStorageProviderCatalog.Resolve(provider).RefreshAccessTokenAsync(...)` to mint a fresh provider access
  token onto the new access token, instead of just carrying the old one forward until it expires. Whatever
  refresh token comes back (Microsoft Entra ID always rotates it on use; Google normally doesn't) is what gets
  cached going forward. Falls back to carrying both forward unchanged only if there's no cached provider refresh
  token, or the provider rejects it (revoked/expired) - at that point wallet calls relying on the (now-stale)
  access token eventually fail `401 storage_access_denied` until the user does a fresh interactive `/authorize`
  sign-in.
- **Also renewed opportunistically inside `WalletController`**
  (`ExecuteWithProviderTokenRefreshAsync`/`TryRefreshProviderAccessTokenAsync`): if a wallet call fails with
  `UnauthorizedAccessException` (the cached provider access token went stale mid-lifetime of an otherwise
  still-valid Biatec token), it renews once from the bearer token's own `provider_refresh_token` claim and
  retries the same call. This renewed token is used for that one request only - it can't be written back into the
  caller's already-issued, signed bearer token, so the next call still resolves the original cached access token
  until the durable fix above (a Biatec refresh_token grant) happens.
- **Consumed** by `WalletController.ResolveProviderAccessToken`: decrypts the bearer token's `provider_token`
  claim (bound to the same email) in-memory, for that one request only. No wallet endpoint has a parameter to
  supply a provider token instead - this is the only mechanism.
- **`Protect`/`Unprotect` fail safe, never loud, per-call**: if the claim is missing/tampered/undecryptable for a
  given request, they return `null` rather than throwing - the caller then sees a normal 401
  `storage_access_denied`, same as any other "not signed in" case.
- **Construction fails loud in production**: `ProviderAccessTokenProtector`'s constructor throws
  `InvalidOperationException` outside `Development` if `ProviderTokenProtection:ActiveKeyId` doesn't resolve to
  a valid key in `Keys[]` (same fail-fast precedent as `JwtIssuerService.LoadOrCreateSigningKey`) - since
  there's no caller-supplied fallback anymore, a misconfigured active key means the wallet API cannot function
  *at all*, and that should be surfaced immediately rather than as a wall of unexplained 401s.
- **Threat model** (full writeup in `OIDC_INTEGRATION_GUIDE.md`'s "Provider access token caching" section): the
  dedicated key ring compartmentalizes this from `AesOptions` (the self-custody file's key ring) and
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
