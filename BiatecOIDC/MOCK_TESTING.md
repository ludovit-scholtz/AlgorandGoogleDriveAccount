# Mock cloud storage testing feature (internal, not for integrators)

This is developer/test tooling built into `BiatecOIDC`. It is **not** part of the public OIDC/wallet API
contract, is never linked from `OIDC_INTEGRATION_GUIDE.md`, `README.md`, `wwwroot/index.html`, or the
`biatec-oidc-jwt` skill doc, and must stay that way — third-party integrators should never be pointed at it.
This file exists purely so a future session (human or Claude) can find and reconfigure it without having to
rediscover the design from scratch.

## What it's for

Lets you run the **entire real OIDC `/authorize` → `/token` flow**, and everything downstream that depends
on a real BiatecOIDC access token (Algorand signing, Ethereum signing, spending limits, seed management,
...), against a synthetic account that:

- needs no real Google/Microsoft sign-in (no external redirect, no consent screen, no real OAuth token),
- derives its Algorand/Ethereum keys from an ARC-76 mnemonic **you choose**, so the resulting address is
  known in advance and reproducible,
- comes back to life with the same address after every app restart, even though its storage is purely
  in-process memory (see "Why it survives restarts" below).

This is for exercising other apps (BiatecMCP, or any third-party OIDC client you're integration-testing)
against a real, running BiatecOIDC instance — not for unit tests inside this repo (those already have their
own fakes/mocks, e.g. `PersistentFakeCloudStorageProvider` in `CloudAccountRepositoryTests.cs`).

## How it works

- **`MockCloudStorageProvider`** (`BiatecSelfCustodyCore/Providers/MockCloudStorageProvider.cs`) is an
  `ICloudStorageProvider` implementation like `GoogleCloudStorageProvider`/`MicrosoftCloudStorageProvider` -
  the same extension point CLAUDE.md documents for adding a new provider. There is no real OAuth token: its
  "access token" is a deterministic `"mock:{email}"` pseudo-token, synthesized from the ambient cookie
  session's own email claim (`GetAmbientAccessTokenAsync`) and parsed back out by
  `TryDownloadAsync`/`UploadAsync`/`DeleteAsync` to know which mock account's files to touch. This is what
  lets every other layer (`ICloudAccountRepository`, `WalletController`, `JwtIssuerService`, ...) treat a
  mock sign-in exactly like a real Google/Microsoft one, with **zero special-casing anywhere outside this one
  class**.
- **`MockCloudStorage`** (`BiatecSelfCustodyCore/Providers/MockCloudStorage.cs`) is the singleton in-memory
  file store backing it - a plain `ConcurrentDictionary<string, byte[]>` keyed by `(email, fileName)`. Reset
  on every process restart, by design.
- **`ICloudAccountRepository.SeedTestVaultAsync(email, provider, mnemonic, accessToken)`**
  (`BiatecSelfCustodyCore/Repository/CloudAccountRepository.cs`) ensures a seed vault entry with *exactly*
  the given mnemonic exists for that email - unlike `CreateSeedAsync`, which always generates a fresh random
  one. It's idempotent: if a seed whose slot-0 address already matches what the mnemonic derives to is
  present, it does nothing. This goes through the *real* vault read/decrypt/derive/encrypt/write path
  (`LoadVaultOrEmptyAsync`/`BuildSeedEntry`/`SaveVaultWithConcurrencyCheckAsync`), so the seed address really
  is produced by the same ARC-76 derivation code every other account uses - nothing here fabricates an
  address directly.
- **Startup seeding** (`BiatecOIDC/Program.cs`, right after `app.Build()`): for every configured account
  under `CloudServices:Mock:Accounts`, calls `SeedTestVaultAsync` once. This is the "why it survives
  restarts" part - the mock storage itself has nothing persisted, but the *configured mnemonic* is the
  actual source of truth, and re-deriving from it is fast and idempotent, so every restart just puts the
  same account back exactly where it was.
- **Sign-in flow** (`BiatecOIDC/Controllers/JwtIssuerController.cs`):
  - `MockCloudStorageProvider` is only registered in DI (`Program.cs`) when `CloudServices:Mock:Enabled` is
    `true` **and** at least one account is configured - so it can never appear as a sign-in option unless
    explicitly turned on.
  - When registered, it does **not** show up on the default `/select-provider` page — the "Mock (Testing)"
    button only renders when the `/authorize` request carried a `scopeId` matching a configured
    `CloudServices:Mock:Accounts` entry (forwarded through the redirect to `/select-provider`), so a real
    user can never stumble into the mock sign-in path from the normal picker. When shown, its button links
    to `GET /authorize/challenge?idp=Mock&requestId=...&scopeId=...` (carrying that same scopeId, so the
    click signs straight into the named account).
  - `AuthorizeChallenge` special-cases `idp=Mock`: instead of `Challenge()`-ing a real external IDP, it
    redirects to `GET /authorize/mock-select-account?requestId=...` - a picker page listing every configured
    account by its `ScopeId` (and email, for clarity).
  - Picking one hits `GET /authorize/mock-sign-in?requestId=...&scopeId=...`, which re-seeds that account
    (harmless no-op if already seeded), builds a `ClaimsPrincipal` with the account's email and a
    `biatec_idp: "Mock"` claim (`CloudStorageProviderClaims.Stamp`, the same call every real provider's
    `OnTokenValidated` makes), signs it into the cookie scheme (`HttpContext.SignInAsync`), and redirects to
    `AuthorizeCallback` - resuming the exact same code path a real provider's OIDC callback lands on.
  - **Fast track**: `GET /authorize?idp=Mock&scopeId=app1&...` (or
    `GET /authorize/challenge?idp=Mock&scopeId=app1`) skips both `/select-provider` and the mock account
    picker and signs in directly - useful for scripting/automated testing without a browser click-through.
    `scopeId` is ignored for every other `idp`.

## Configuring test accounts

Add to `appsettings.json` (or an environment-specific override / env vars, same as any other config in this
app) under `CloudServices:Mock`:

```json
"CloudServices": {
  "Mock": {
    "Enabled": true,
    "Accounts": [
      {
        "ScopeId": "app1",
        "Email": "mock-app1@biatec.test",
        "Mnemonic": "word1 word2 ... word25"
      },
      {
        "ScopeId": "app2",
        "Email": "mock-app2@biatec.test",
        "Mnemonic": "word1 word2 ... word25 (a different 25-word Algorand mnemonic)"
      }
    ]
  }
}
```

- `ScopeId` - whatever you want to call this test identity (shown on the picker page, and passed as
  `scopeId` on the fast-track URL). Keep it unique per entry; if you configure a new app to test, add a new
  entry with a new `ScopeId` rather than reusing one already in use.
- `Email` - purely a key/label; doesn't need to be a real mailbox. Give each account its own distinct email.
- `Mnemonic` - a valid 25-word Algorand mnemonic (e.g. generate one with `goal account new` or the Algorand
  SDK's `Account()` constructor + `.ToMnemonic()` - see `BuildSeedEntry` in `CloudAccountRepository.cs` for
  the exact derivation this mirrors). This **is** the ARC-76 secret - anyone with it can derive and sign
  with that account's key, so treat it with the same care as any other secret, even though it's only a test
  account.

The checked-in `appsettings.json` ships with `CloudServices:Mock:Enabled: false` and an empty `Accounts`
array - safe by default, and never active in production unless someone deliberately flips it on and adds
real entries. `appsettings.json` is committed to source control, so for anything beyond a
throwaway/ephemeral test mnemonic, prefer overriding `CloudServices:Mock` via environment variables or a
local, git-ignored config file instead of editing the committed file directly.

## Using it end to end

1. Set `CloudServices:Mock:Enabled: true` and add at least one account (above). Restart the app - watch the
   startup log for `"Seeded mock test account '{ScopeId}' ({Address})"` to confirm it worked and see the
   resulting address.
2. Drive a normal OIDC `response_type=code` flow against `/authorize`, either:
   - through the browser: `?idp=Mock` to land on the mock account picker (or pass `?scopeId=app1` without
     `idp` to land on the normal `/select-provider` page with a "Mock (Testing)" button next to the real
     providers — without a configured `scopeId`, the mock button never appears there), or
   - fully scripted: `?idp=Mock&scopeId=app1` skips every picker and signs in immediately.
3. Exchange the returned code at `/token` as usual - the resulting access/ID token is a **completely normal,
   real, signed Biatec token** (same claims, same `primary_seed_address`, same everything) - there is
   nothing "fake" about the token itself, only the identity provider behind it.
4. Use that token exactly like any other: call `GET /wallet/address/{seedAddress}/{slot?}` to derive the
   Algorand/Ethereum addresses, `POST /wallet/{network}/{address}/sign` to sign (needs the `sign` scope
   allowlisted for whichever client you authorized as, same as any real client), etc. Since the seed's
   mnemonic is one you chose, you always know in advance which address you're signing with/testing against.

## What this does *not* do

- No cross-cloud vault backup for the Mock provider - `BuildAuthorizationUrl`/`ExchangeAuthorizationCodeAsync`
  both throw `NotSupportedException`. Backing up a mock account anywhere doesn't make sense (there's nothing
  real to protect), and this was never a requirement.
- No real OAuth token to refresh - `GetAmbientRefreshTokenAsync` always returns `null`; the mock access
  token never expires (it's just a deterministic function of the signed-in email), so there's nothing to
  renew.
- Storage-write-access verification (`HasWriteAccessAsync`) is always `true` for a mock token - there is no
  consent to decline in the first place.
