# Biatec MCP Server — Risk Registry

This is the living, cumulative risk registry for the Biatec MCP Server (self-custody Algorand key storage in user
Google Drive + OIDC identity provider). It is maintained by whoever performs a deep security audit under
[AUDITS-INSTRUCTIONS.md](AUDITS-INSTRUCTIONS.md) — see that file for the rules governing how entries are added,
revised, and closed, and for the meaning of the likelihood percentage.

Populated by the first audit:
[audit-report-2026-23-07-20902ee-claude-code-ai-review.md](audit-report-2026-23-07-20902ee-claude-code-ai-review.md).
**Note on that audit's status:** it was performed by an AI coding assistant with the same repository access as the
development team, not an independent third-party firm — see that report's front matter for the full disclosure.
Likelihood estimates below should be treated as a first-pass, first-party estimate pending independent
confirmation.

**2026-07-24 remediation note:** Engineering (not a new independent audit) implemented fixes for every open risk
below except R-013, in the same commit series that added this note. Each remediated entry's History records what
changed and why. This is first-party engineering work responding to the first audit's findings — it does **not**
constitute a new independent audit, and does not itself re-verify the absence of new defects introduced by the
fix. An independent audit engagement should still re-verify these fixes (per `AUDITS-INSTRUCTIONS.md`'s cadence
rule: "before any material change to `AesEncryptionHelper.cs`... ships to production") before this registry's
"Closed" status is relied upon as external assurance.

**2026-07-24 second audit note:** A second engagement
([audit-report-2026-24-07-173793a-claude-code-ai-review-2.md](audit-report-2026-24-07-173793a-claude-code-ai-review-2.md),
signature `claude-code-ai-review-2`, same reviewing party/method as the first audit — see that report's own
conflict-of-interest disclosure) independently re-verified the 2026-07-24 remediation pass's claims against the
actual code (not just the remediation pass's own description), and ran the build/test suite as evidence. Fourteen
of sixteen prior findings were confirmed genuinely fixed. R-011's fix was confirmed genuine for its originally-cited
components but found not to extend to `BiatecMCPGoogle.cs` (new entry R-018 opened for that gap rather than
reopening R-011). `k8s/main/conf/*` was inspected for the first time (new entry R-019 opened; R-013 left otherwise
unchanged since GitHub branch-protection settings remain unverifiable from repository content alone).

## Open risks

### R-013 — CI/CD pipeline has no in-workflow deployment gate; `k8s/main/conf` contents unverified

- **Description:** `build-api.yml` deploys directly to production on every push to `master` with no `environment:`
  protection block visible in the workflow file itself; `k8s/main/conf/*` was not inspected for accidentally
  plaintext secret material in this audit.
- **Likelihood (5-year misuse probability):** 10% — reasoning: depends entirely on external GitHub
  branch-protection configuration not verifiable from repository content; supply-chain/CI compromise is a
  recurring real-world incident category industry-wide, and this pipeline's blast radius (direct production
  deploy) is high if branch protection is ever misconfigured or lapses.
- **Impact:** A compromised or unreviewed push to `master` would deploy directly to production with no additional
  gate; if `k8s/main/conf` contains secret material, it would be exposed at a lower protection tier than intended.
- **Affected component:** `.github/workflows/build-api.yml`; `k8s/main/conf/*` (unverified).
- **Current mitigations:** `docs/KUBE_CONFIG_SECURITY.md`-described namespace-scoped, time-limited kubeconfig
  limits blast radius of the CI credential itself even if the pipeline is triggered maliciously.
- **Recommended further mitigation:** Verify/require GitHub branch-protection on `master`; audit `k8s/main/conf/*`
  contents for secret material. **Not a code change** — requires manually checking GitHub repository settings and
  the contents of `k8s/main/conf/*`, neither of which this remediation pass could perform.
- **Status:** Open.
- **History:**
  - 2026-23-07 — claude-code-ai-review: opened at 10%, corresponds to finding F-13.
  - 2026-07-24 — engineering-remediation: left open; flagged to the repository owner as a manual follow-up
    (branch-protection settings and `k8s/main/conf/*` review are outside what a code change can verify or fix).
  - 2026-24-07 — claude-code-ai-review-2: likelihood left unchanged at 10% — GitHub branch-protection settings for
    `master` remain unverifiable from repository content alone, and nothing has changed about that specific gap
    since the last estimate. `k8s/main/conf/*` was inspected this pass, as flagged; a specific, more severe issue
    found there (a possibly-live AES key/IV committed unchanged for over a year) is tracked separately as **R-019**
    rather than folded into this entry's likelihood, since it is a distinct, more concrete concern than the general
    "contents unverified" note this entry originally carried.

## Closed risks

### R-001 — Weak, client-generated device-pairing session IDs exposed via unauthenticated endpoints (IDOR)

- **Description:** `pair.html`'s `generateSessionId()` built the device-pairing session ID from `Date.now()` +
  `Math.random()` (non-cryptographic PRNG) in the browser. This ID was the sole Redis lookup key protecting a
  user's Google OAuth tokens, and several `DevicePairingController` endpoints (`access-token`, `diagnose`,
  `security-status`, `portfolio`, `unpair`) were `[AllowAnonymous]` with no ownership check and no rate limiting,
  keyed only on this ID.
- **Affected component:** `AlgorandGoogleDriveAccount/wwwroot/pair.html`;
  `AlgorandGoogleDriveAccount/Controllers/DevicePairingController.cs`.
- **Mitigation implemented:**
  - `pair.html`'s `generateSessionId()` now uses the Web Crypto API (`crypto.getRandomValues`, 32 bytes / 256
    bits of entropy) instead of `Date.now()+Math.random()`. The pairing session ID is a bearer secret, and is
    now unguessable in practice — the same trust model `JwtIssuerService` already uses for refresh tokens.
  - All `{sessionId}`-keyed `DevicePairingController` endpoints now carry `[EnableRateLimiting("device-session")]`
    (a per-client-IP fixed-window limiter registered in `Program.cs`), as defense-in-depth against brute force
    even against the new high-entropy IDs.
  - `/api/device/diagnose/{sessionId}` (leftover troubleshooting tooling disclosing Drive folder/file metadata
    beyond what the pairing feature needs) now returns 404 outside `IHostEnvironment.IsDevelopment()`, removing
    it from the production attack surface while keeping it available for local debugging.
  - Verbose error messages on the remaining session-keyed endpoints were sanitized (see R-011).
- **Status:** Closed (mitigated).
- **History:**
  - 2026-23-07 — claude-code-ai-review: opened at 35%, corresponds to finding F-01.
  - 2026-07-24 — engineering-remediation: closed. CSPRNG session ID, rate limiting, and Dev-only `diagnose`
    gating implemented; regression tests added in `DevicePairingControllerTests.cs`. Recommend an independent
    audit re-verify before this is relied upon as external assurance.
  - 2026-24-07 — claude-code-ai-review-2: independently confirmed. `pair.html:222-230` uses
    `crypto.getRandomValues` (32 bytes); `[EnableRateLimiting("device-session")]` confirmed present on all seven
    originally-named endpoints plus the new `receiver-allowlist` endpoint, with the limiter correctly wired via
    `app.UseRateLimiter()`; `DiagnoseAccount` confirmed to return `NotFound()` before calling the underlying
    service outside Development, verified via both direct reading and a passing test that asserts the service is
    never invoked. Status/likelihood unchanged; closure affirmed with independent evidence.

### R-002 — AES key/IV derivation is a single global-secret hash, not a per-user KDF; no authenticated encryption

- **Description:** All users' encrypted mnemonic files shared one global `AesOptions.Key`/`IV` pair, with
  per-user differentiation coming only from a single unsalted SHA-256 hash of `key||email` (not a real KDF).
  Encryption was AES-CBC with no HMAC/AEAD, and decryption-failure error messages were verbose enough to act as a
  padding-error oracle.
- **Affected component:** `AlgorandGoogleDriveAccount/Helper/AesEncryptionHelper.cs`;
  `AlgorandGoogleDriveAccount/Repository/GoogleDriveRepository.cs` (error handling).
- **Mitigation implemented:** `AesEncryptionHelper` now writes a versioned, authenticated format: a random
  16-byte per-file salt feeds an HKDF-SHA256 per-file key derivation (replacing the single unsalted SHA-256 hash
  of the shared secret), and AES-256-GCM provides both confidentiality and tamper detection (replacing
  unauthenticated CBC). A magic prefix (`BIATECV2`) lets `Decrypt` distinguish the new format from the legacy
  one; **every file already encrypted under the old scheme keeps decrypting correctly** via an unchanged legacy
  CBC/SHA-256 fallback path — no migration needed. Decrypt-failure messages returned to API callers
  (`GoogleDriveRepository`, `DriveController`) no longer include email, file size, or raw exception text (see
  R-011); full detail is logged server-side only.
- **Status:** Closed (mitigated).
- **History:**
  - 2026-23-07 — claude-code-ai-review: opened at 15%, corresponds to finding F-02. Impact is rated far higher
    than likelihood because a realized event would affect the entire user base simultaneously, not one account.
  - 2026-07-24 — engineering-remediation: closed. Versioned AES-GCM format with per-file HKDF-derived keys and
    backward-compatible legacy decryption implemented; round-trip, legacy-fixture, tamper-detection, and
    wrong-email tests added in `AesEncryptionHelperTests.cs`. Recommend an independent audit re-verify given this
    touches the most security-critical file in the codebase per `AUDITS-INSTRUCTIONS.md`.
  - 2026-24-07 — claude-code-ai-review-2: algorithm-level fix independently confirmed — fresh random 12-byte GCM
    nonce and 16-byte salt generated per `Encrypt` call (`AesEncryptionHelper.cs:37-38`), confirmed non-reused via
    the passing `Encrypt_ProducesDifferentCiphertextEachCall` test; `Decrypt`'s legacy-format fallback is selected
    only by inspecting already-at-rest ciphertext bytes, not attacker- or caller-controllable at encryption time,
    so it is not a new downgrade vector; new writes always use the authenticated format. However, this audit found
    that the *shared base secret* fed into the new HKDF scheme (`AesOptions.Key`/`IV`) has a syntactically
    real-looking, unchanged-for-a-year value committed in `k8s/main/conf/appsettings.json`, which is the same class
    of single-point-of-failure this risk's impact reasoning already describes. Status remains Closed (the
    algorithm-level defect this entry describes is genuinely fixed), but the underlying base-secret concern is now
    tracked with its own severity and evidence as new entry **R-019** rather than reopening this one.

### R-003 — JWT bearer access-token validation does not check the `aud` claim

- **Description:** `ValidateBearerAccessToken` set `ValidateAudience = false`, so an access token issued to one
  OIDC client was accepted at `/userinfo`, `/introspect`, `/verify` regardless of its actual `aud` claim.
- **Affected component:** `AlgorandGoogleDriveAccount/BusinessLogic/JwtIssuerService.cs`.
- **Mitigation implemented:** `ValidateBearerAccessToken` now sets `ValidateAudience = true` with
  `ValidAudiences` = the set of currently-registered client IDs (access tokens' `aud` is always the requesting
  `client_id`, confirmed by the existing `AccessToken_AudienceIsClientId` test, which still passes unmodified).
  A token whose client has since been deregistered, or whose `aud` doesn't match any registered client, is now
  rejected at all three endpoints (they share this one validation method).
- **Status:** Closed (mitigated).
- **History:**
  - 2026-23-07 — claude-code-ai-review: opened at 10%, corresponds to finding F-03. Likelihood expected to rise as
    the number of distinct integrated OIDC clients grows — revisit at next audit.
  - 2026-07-24 — engineering-remediation: closed. Audience validation enabled; regression tests added
    (`ValidToken_ClientSubsequentlyDeregistered_ReturnsIsValidFalse`,
    `TokenWithForeignAudience_ReturnsIsValidFalse`) alongside the existing positive-path audience test.
  - 2026-24-07 — claude-code-ai-review-2: independently confirmed. `ValidateBearerAccessToken`
    (`JwtIssuerService.cs:494-536`) sets `ValidateAudience = true` with `ValidAudiences =
    Current.Clients.Select(c => c.ClientId)`. Status/likelihood unchanged.

### R-004 — MCP fund-transfer tools broadcast on-chain transactions with no server-side confirmation gate

- **Description:** `TransferAsset`/`OptIn` MCP tools signed and broadcast immediately based solely on the
  (weak, see R-001) session ID, with no confirmation step, spending limit, or receiver allowlist enforced
  server-side.
- **Affected component:** `AlgorandGoogleDriveAccount/MCP/BiatecMCPGoogle.cs`.
- **Mitigation implemented:** Per explicit product decision, a two-step confirmation-token flow was **not**
  implemented (it would change the MCP tool calling contract); instead:
  - A configurable, server-enforced spend ceiling (`McpTransferLimits:MaxAmount` in `appsettings.json`, default
    `0` = unbounded — no behavior change unless an operator opts in) rejects `transferAsset` calls over the
    configured amount before any Drive/Algod/credential work happens.
  - An optional, per-paired-session receiver allowlist (`POST /api/device/receiver-allowlist/{sessionId}`,
    rate-limited like the other session endpoints) lets a user restrict which addresses `transferAsset` may send
    to; empty/unset (the default) means unrestricted.
  - This narrows, but does not eliminate, the prompt-injection-to-theft risk: an attacker who can inject
    instructions into a legitimately-paired agent can still direct a transfer within any configured limit/
    allowlist. Residual risk should be tracked at the next audit.
- **Status:** Closed (mitigated, not eliminated — see note above).
- **History:**
  - 2026-23-07 — claude-code-ai-review: opened at 25%, corresponds to finding F-04.
  - 2026-07-24 — engineering-remediation: closed as mitigated (spend ceiling + optional receiver allowlist,
    per product decision against a two-step confirmation flow). Pure-function checks extracted to
    `Helper/TransferPolicy.cs` and unit-tested in `TransferPolicyTests.cs`. Residual prompt-injection risk within
    configured limits remains — recommend the next audit re-assess likelihood given this partial mitigation.
  - 2026-24-07 — claude-code-ai-review-2: independently confirmed. `TransferAsset` (`BiatecMCPGoogle.cs:154-173`)
    checks `TransferPolicy.ExceedsMaxAmount` and `TransferPolicy.IsReceiverAllowed` before any Drive/Algod/
    credential work; both are correctly-ordered (limit/allowlist checked first, cheapest rejection path).
    Likelihood left unchanged from the remediation pass's implicit "mitigated, not eliminated" framing — this
    audit did not attempt to re-derive a numeric estimate since the prior entry did not record one to revise, and
    the residual risk (prompt injection within configured limits) is unchanged in nature.

### R-005 — Authorization-code / pending-authorize-request redemption is not atomic

- **Description:** Redis get-then-delete for one-time codes and pending authorize requests was not atomic,
  permitting a narrow-window double-redemption race.
- **Affected component:** `AlgorandGoogleDriveAccount/BusinessLogic/JwtIssuerService.cs`.
- **Mitigation implemented:** Authorization-code, refresh-token, and pending-authorize-request consumption now
  use an atomic Redis `GETDEL` (`IDatabase.StringGetDeleteAsync`, via a directly-injected
  `IConnectionMultiplexer` alongside the existing `IDistributedCache`), so a concurrent second redemption attempt
  can never observe the value as still present.
- **Status:** Closed (mitigated).
- **History:**
  - 2026-23-07 — claude-code-ai-review: opened at 5%, corresponds to finding F-05.
  - 2026-07-24 — engineering-remediation: closed. Atomic GETDEL implemented for codes, refresh tokens, and
    pending requests; existing double-redemption tests updated to assert the atomic call, per-behavior otherwise
    unchanged (all pre-existing `ExchangeTokenAsync` tests pass unmodified).
  - 2026-24-07 — claude-code-ai-review-2: independently confirmed. `GetAndDeleteAsync`
    (`JwtIssuerService.cs:306-311`) uses `IDatabase.StringGetDeleteAsync` (Redis `GETDEL`), applied uniformly to
    authorization codes, refresh tokens, and pending authorize requests. Status/likelihood unchanged.

### R-006 — Non-constant-time client-secret comparison

- **Description:** `ValidateClientAuthentication` compared client secrets with `string.Equals(...,
  StringComparison.Ordinal)`, a short-circuiting, non-constant-time comparison.
- **Affected component:** `AlgorandGoogleDriveAccount/BusinessLogic/JwtIssuerService.cs`.
- **Mitigation implemented:** Client-secret comparison now uses `CryptographicOperations.FixedTimeEquals` over
  UTF-8 byte arrays (length-checked first, since `FixedTimeEquals` requires equal-length spans). Public clients
  (no configured secret) are unaffected — that branch is unchanged.
- **Status:** Closed (mitigated).
- **History:**
  - 2026-23-07 — claude-code-ai-review: opened at 3%, corresponds to finding F-06.
  - 2026-07-24 — engineering-remediation: closed. `FixedTimeEquals`-based comparison implemented; added
    `WrongClientSecret_SameLengthAsCorrectSecret_StillRejected` regression test.
  - 2026-24-07 — claude-code-ai-review-2: independently confirmed. `FixedTimeSecretsEqual`
    (`JwtIssuerService.cs:739-746`) checks byte-array length equality (an accepted, standard pre-check for this
    API since `FixedTimeEquals` requires equal-length inputs, and secret length is not itself treated as
    sensitive) then calls `CryptographicOperations.FixedTimeEquals`. Status/likelihood unchanged.

### R-007 — Silent fallback to an ephemeral JWT signing key on configuration failure

- **Description:** Missing/misconfigured `JwtIssuer:SigningPrivateKeyPem` degraded to an in-memory ephemeral RSA
  key with only a warning log, rather than failing startup.
- **Affected component:** `AlgorandGoogleDriveAccount/BusinessLogic/JwtIssuerService.cs`.
- **Mitigation implemented:** `JwtIssuerService` now takes an `IHostEnvironment` dependency;
  `LoadOrCreateSigningKey` throws `InvalidOperationException` at startup when no valid signing key is configured
  in any non-Development environment, instead of silently falling back to an ephemeral key. The ephemeral-key
  fallback (with its warning log) is preserved for Development only, so local/dev workflows are unaffected.
- **Status:** Closed (mitigated).
- **History:**
  - 2026-23-07 — claude-code-ai-review: opened at 8%, corresponds to finding F-07.
  - 2026-07-24 — engineering-remediation: closed. Production/non-Development fail-fast implemented; tests added
    for both the Production-throws and Development-still-falls-back behaviors.
  - 2026-24-07 — claude-code-ai-review-2: independently confirmed. `LoadOrCreateSigningKey`
    (`JwtIssuerService.cs:781-815`) throws `InvalidOperationException` when `!_environment.IsDevelopment()` and no
    valid PEM key is configured; ephemeral-key fallback with a warning log is preserved only for Development.
    Status/likelihood unchanged. Note (informational, not a reopening): no equivalent fail-fast check exists yet
    for `AesOptions.Key`/`IV` — see new entry **R-019**, which recommends extending this same pattern there.

### R-008 — Open redirect on `/api/drive/login` and `/api/drive/logout`

- **Description:** `redirectUri` query parameter on both actions was, per the original audit, passed unvalidated
  into `AuthenticationProperties.RedirectUri`.
- **Affected component:** `AlgorandGoogleDriveAccount/Controllers/DriveController.cs`.
- **Investigation finding (2026-07-24):** This was **already fixed** in the codebase by the time this
  remediation pass began (commit `6c74f2f`, after the audited commit `20902ee`) —
  `DriveController.ResolveLocalRedirectUri` validates `redirectUri` via `Url.IsLocalUrl(...)` and falls back to
  `~/swagger/` otherwise; there was no remaining code change to make. This entry is closed to reflect that fix,
  not the 2026-07-24 remediation pass.
- **Status:** Closed (was already mitigated prior to this remediation pass).
- **History:**
  - 2026-23-07 — claude-code-ai-review: opened at 12%, corresponds to finding F-08.
  - 2026-07-24 — engineering-remediation: closed. Confirmed already fixed by commit `6c74f2f`
    (`Url.IsLocalUrl`-based validation); added regression tests (`DriveControllerTests.cs`) to prevent
    recurrence, since no test previously covered this behavior.
  - 2026-24-07 — claude-code-ai-review-2: independently confirmed. `ResolveLocalRedirectUri`
    (`DriveController.cs:158-166`) returns the caller-supplied `redirectUri` only if `Url.IsLocalUrl(redirectUri)`
    is true, else falls back to `~/swagger/`. Status/likelihood unchanged.

### R-009 — Cross-Account Protection disabled by default; `email_verified` not enforced at runtime by default

- **Description:** RISC-style checks (including `email_verified`) never ran unless
  `CrossAccountProtection:Enabled` was explicitly turned on, which it is not by default.
- **Affected component:** `AlgorandGoogleDriveAccount/Model/Configuration.cs`;
  `AlgorandGoogleDriveAccount/BusinessLogic/CrossAccountProtectionService.cs`.
- **Mitigation implemented:** `Program.cs`'s Google OpenIdConnect `OnTokenValidated` handler now fails
  authentication if the `id_token`'s `email_verified` claim is present and `false`, unconditionally — independent
  of the optional Cross-Account Protection feature toggle. This closes the gap for the core tenant-isolation
  guarantee without requiring CAP to be enabled.
- **Status:** Closed (mitigated).
- **History:**
  - 2026-23-07 — claude-code-ai-review: opened at 6%, corresponds to finding F-09.
  - 2026-07-24 — engineering-remediation: closed. Unconditional `email_verified` enforcement added in
    `Program.cs`'s OIDC event handler. Not independently unit-tested (ASP.NET Core authentication-handler wiring
    in `Program.cs` isn't practically unit-testable in isolation) — recommend manual/integration verification.
  - 2026-24-07 — claude-code-ai-review-2: independently confirmed. `OnTokenValidated`
    (`Program.cs:295-307`) calls `context.Fail(...)` (a genuine authentication-failure call per the
    `RemoteAuthenticationContext` contract, not a no-op/log-only path) when the `email_verified` claim is present
    and equals `"false"`. The remediation pass's own disclosed gap (no unit test for this handler wiring, since
    ASP.NET Core auth pipelines are impractical to unit-test in isolation) is unchanged and not newly closed by
    this audit. Status/likelihood unchanged.

### R-010 — `HasScopeAsync` does not check the actual granted scope

- **Description:** Returned `true` for any non-empty access token regardless of the `scope` parameter requested.
- **Affected component:** `AlgorandGoogleDriveAccount/BusinessLogic/GoogleAuthorizationService.cs`.
- **Mitigation implemented:** `HasScopeAsync` now calls Google's `tokeninfo` endpoint and checks the token's
  actual granted `scope` list before returning `true`, failing closed (`false`) if the check itself fails. Note:
  this method had no callers anywhere in the codebase at the time of the original audit or this remediation —
  the fix ensures correctness for whenever it is wired up, per the audit's recommendation.
- **Status:** Closed (mitigated).
- **History:**
  - 2026-23-07 — claude-code-ai-review: opened at 4%, corresponds to finding F-10.
  - 2026-07-24 — engineering-remediation: closed. Real scope check via Google's tokeninfo endpoint implemented;
    tests added in `GoogleAuthorizationServiceTests.cs` (scope present/absent/tokeninfo-failure cases).
  - 2026-24-07 — claude-code-ai-review-2: independently confirmed. `TokenGrantsScopeAsync`
    (`GoogleAuthorizationService.cs:109-129`) calls Google's `tokeninfo` endpoint and fails closed (returns
    `false`) on any non-success response or missing `scope` property. Still has no callers in the codebase
    (confirmed via search), consistent with the original finding. Status/likelihood unchanged.

### R-011 — Verbose internal error messages surfaced in API responses

- **Description:** Decryption and other internal exception messages (email, file size, raw exception text) were
  returned directly in HTTP response bodies across several controllers.
- **Affected component:** `AlgorandGoogleDriveAccount/Repository/GoogleDriveRepository.cs`;
  `DriveController.cs`; `DevicePairingController.cs`.
- **Mitigation implemented:** All identified `exc.Message`/`ex.Message` passthroughs in `GoogleDriveRepository`'s
  decrypt-failure path, `DriveController`'s generic exception handlers, and `DevicePairingController`'s
  `diagnose`/`security-status`/`report-security-event`/`portfolio` handlers now log full detail server-side
  (`ILogger`) and return a generic, non-identifying message to the caller.
- **Status:** Closed (mitigated).
- **History:**
  - 2026-23-07 — claude-code-ai-review: opened at 15%, corresponds to finding F-11.
  - 2026-07-24 — engineering-remediation: closed. Sanitized across `GoogleDriveRepository`, `DriveController`,
    and `DevicePairingController`; covered by existing/updated tests in those areas (no test previously asserted
    on the raw message content, so no regression risk from this change).
  - 2026-24-07 — claude-code-ai-review-2: independently confirmed genuine for the three originally-cited
    components. Also checked `BiatecMCPGoogle.cs` (in scope per `AUDITS-INSTRUCTIONS.md`, not one of the three
    components this entry names) and found the same sanitization was **not** applied there — `GetAccountAddress`,
    `TransferAsset`, and `OptIn`'s generic catch blocks still return raw `ex.Message`/`ex.Result.Message` in MCP
    tool responses. This does not change this entry's status (it is accurately closed for its own stated scope);
    the gap is tracked as new entry **R-018** rather than reopening this one.

### R-012 — Drive search-query built via unescaped string interpolation (not currently attacker-reachable)

- **Description:** `folderRequest.Q` interpolated configuration-sourced values without escaping; not reachable by
  user input at the time of the audit.
- **Affected component:** `AlgorandGoogleDriveAccount/Repository/GoogleDriveRepository.cs`.
- **Mitigation implemented:** Added an escaping helper (Drive query syntax: `'` → `\'`, `\` → `\\`) and applied it
  to `folderName`/`fileName`/`folder.Id` wherever they're interpolated into a Drive `q` search string, in both
  `GoogleDriveRepository.cs` and the equivalent inline query in `DevicePairingController.DiagnoseAccount`. Purely
  defensive — no user-controlled input reaches this code path today.
- **Status:** Closed (mitigated).
- **History:**
  - 2026-23-07 — claude-code-ai-review: opened at 2%, corresponds to finding F-12.
  - 2026-07-24 — engineering-remediation: closed. Escaping helper added and applied at both call sites.
  - 2026-24-07 — claude-code-ai-review-2: independently confirmed. `EscapeDriveQueryValue`
    (`GoogleDriveRepository.cs:34`) is applied to `folderName`, `fileName`, and `folder.Id` at both Drive query
    sites in that file, and the equivalent inline escaping is present in
    `DevicePairingController.DiagnoseAccount:377`. Status/likelihood unchanged.

### R-014 — `id_token_hint` audience trusted without signature validation at `/connect/endsession`

- **Description:** `TryGetClientIdFromIdTokenHint` read `aud` from an unvalidated JWT solely to pick which
  registered client's allowlist to check the real redirect target against.
- **Affected component:** `AlgorandGoogleDriveAccount/BusinessLogic/JwtIssuerService.cs`.
- **Mitigation implemented:** Replaced the unauthenticated `ReadJwtToken`-only parse with a new
  `IJwtIssuerService.TryGetAudienceFromSelfIssuedToken` method that validates the `id_token_hint`'s signature and
  issuer against this provider's own signing key (lifetime intentionally not checked, since a logout hint
  legitimately references an already-expired `id_token`) before trusting its `aud` claim; returns `null` (hint
  ignored) on any validation failure.
- **Status:** Closed (mitigated).
- **History:**
  - 2026-23-07 — claude-code-ai-review: opened at 3%, corresponds to finding F-14.
  - 2026-07-24 — engineering-remediation: closed. Signature/issuer validation added; tests added
    (`GenuineSelfIssuedIdToken_ReturnsAudience`, `TamperedIdTokenHint_ReturnsNull`, `GarbageToken_ReturnsNull`).
  - 2026-24-07 — claude-code-ai-review-2: independently confirmed. `TryGetAudienceFromSelfIssuedToken`
    (`JwtIssuerService.cs:538-561`) validates `ValidateIssuerSigningKey`/`ValidateIssuer` (deliberately not
    lifetime, since a logout hint legitimately references an already-expired `id_token`) before trusting the
    token's `aud` claim, returning `null` on any validation failure. Status/likelihood unchanged.

_(Note: R-015 is intentionally not used. Finding F-15 in the audit report — review of `RedirectUriMatcher` — found
no bypass and is a confirmation of a control working correctly, not a risk; it is documented in the report but has
no corresponding registry entry.)_

### R-016 — Possible captive-dependency between singleton `GoogleDriveRepository` and `IGoogleAuthProvider` (unconfirmed → not reproducible)

- **Description:** If `IGoogleAuthProvider` were Scoped-lifetime in `Google.Apis.Auth.AspNetCore3`, its injection
  into the singleton `GoogleDriveRepository` could capture the first request's credential for all subsequent
  requests using the implicit-credential code path (`/api/drive/sign`, `/api/drive/address`).
- **Affected component:** `AlgorandGoogleDriveAccount/Repository/GoogleDriveRepository.cs`.
- **Investigation finding (2026-07-24):** Resolved by direct evidence. The installed
  `Google.Apis.Auth.AspNetCore3` 1.75.0 package's IL was disassembled (its `GoogleOpenIdConnectExtensions.
  AddGoogleOpenIdConnect` DI registration method), confirming `IGoogleAuthProvider` is registered via
  `AddSingleton<IGoogleAuthProvider, GoogleAuthProvider>` — **not** Scoped. There is no captive-dependency issue:
  both the consumer (`GoogleDriveRepository`) and the dependency (`GoogleAuthProvider`) are Singletons, and
  `GoogleAuthProvider` itself resolves per-request state from `IHttpContextAccessor` internally at call time
  rather than capturing it at construction.
- **Status:** Closed (not reproducible — confirmed non-issue by direct package inspection).
- **History:**
  - 2026-23-07 — claude-code-ai-review: opened as unconfirmed, corresponds to finding F-16.
  - 2026-07-24 — engineering-remediation: closed as not reproducible, based on IL-level confirmation of
    `IGoogleAuthProvider`'s Singleton registration in the installed package version. No code change was needed
    or made.
  - 2026-24-07 — claude-code-ai-review-2: did not re-run the IL disassembly (see this audit's report,
    Methodology deviations, for reasoning). Independently re-confirmed the structural fact this audit could check
    without it: `GoogleDriveRepository` holds no mutable per-request instance state — `email`, `slot`, and
    `googleCredential` are passed as method parameters to `LoadAccount`, never stored on `this`. The
    `Google.Apis.Auth.AspNetCore3` package version is unchanged since the first audit (confirmed via `.csproj`
    diff), so the prior IL-level fact has no reason to have changed either. Status/likelihood unchanged.

### R-018 — MCP tool responses (`BiatecMCPGoogle.cs`) still return raw internal exception messages

- **Description:** R-011's fix sanitized verbose/raw exception messages returned by `GoogleDriveRepository`,
  `DriveController`, and `DevicePairingController`, but the same class of issue remains in the MCP tool surface:
  `GetAccountAddress`, `TransferAsset`, and `OptIn` (`AlgorandGoogleDriveAccount/MCP/BiatecMCPGoogle.cs`) all
  return raw `ex.Message` (and, for Algorand API errors, `ex.Result.Message`) directly in their tool response to
  the connected AI client, via their generic `catch (Exception ex)` blocks.
- **Affected component:** `AlgorandGoogleDriveAccount/MCP/BiatecMCPGoogle.cs` (`GetAccountAddress:117-120`,
  `TransferAsset:237-244`, `OptIn:327-334`, plus the `ex.Result.Message` passthroughs at `:212-216`/`:299-306`).
- **Likelihood (5-year misuse probability):** 5% — reasoning: unlike the R-011 endpoints, this surface is only
  reachable by an already-paired, already-authenticated MCP client for that specific session, not an arbitrary
  unauthenticated caller, so the exposure is to a narrower audience with a legitimate reason to already have
  session access. Likelihood is non-zero because the "audience" is the third-party AI service/host running the
  connected agent, which is a different trust boundary than this server's own logs, and error text could
  incidentally reveal internal implementation details useful for a follow-on attack (e.g., library versions,
  internal error codes) to a party the user did not necessarily intend to share that with.
- **Impact:** Low-to-Medium. Does not expose key material or session credentials directly, but is inconsistent
  with the sanitization standard applied elsewhere and could leak implementation details to the hosting AI
  service.
- **Current mitigations:** None specific to this path; general MCP session-pairing (R-001's fix) limits who can
  reach this surface at all.
- **Recommended further mitigation:** Apply the same log-full-detail-server-side / return-generic-message pattern
  used for R-011's three components to `BiatecMCPGoogle.cs`'s three MCP tools, while preserving legitimate
  user-facing Algorand API error text (e.g. "insufficient balance") as distinct from raw `.NET` exception text.
- **Status:** Open.
- **History:**
  - 2026-24-07 — claude-code-ai-review-2: opened at 5%, corresponds to finding G-01 in
    [audit-report-2026-24-07-173793a-claude-code-ai-review-2.md](audit-report-2026-24-07-173793a-claude-code-ai-review-2.md).
    Discovered while independently re-verifying R-011's closure — the fix was found genuine for R-011's own
    originally-cited scope but was not extended to this file.

### R-019 — Committed `k8s/main/conf/appsettings.json` contains AES key/IV material of unconfirmed live status

- **Description:** The Kubernetes `ConfigMap` source file that the CI pipeline deploys to production
  (`.github/workflows/build-api.yml`: `kubectl create configmap google-account-main-conf --from-file=k8s/main/conf`)
  contains a syntactically valid, correctly-sized AES-256 key and IV
  (`k8s/main/conf/appsettings.json:11-14`), annotated with a comment claiming it is overridden by a Kubernetes
  `Secret` (`google-account-main-app-secret`, referenced via `envFrom` in `k8s/main/deployment-main.yaml`) at
  deploy time. `git log --follow -p` confirms this exact value has been present, byte-for-byte unchanged, since
  the deployment config's first commit (`d3b9a19`) over a year ago. `ConfigMap` objects (unlike `Secret` objects)
  are not encrypted at rest by default. This is the same shared-global-secret single-point-of-failure R-002's
  impact reasoning already describes (a leaked `AesOptions.Key`/`IV` value defeats confidentiality for every
  user's stored mnemonic, given only their non-secret email address) — R-002's algorithm-level fix (per-file HKDF
  salt, AES-GCM) does not change this, since the shared value is still the `baseValue` fed into that HKDF.
  Unlike the `ClientId`/`ClientSecret`/Redis-connection-string placeholders in the same file (obviously
  non-functional strings that would loudly fail if not overridden), the `AesOptions` value is not visually
  distinguishable from real production key material, and there is no startup check (unlike R-007's fix for the
  JWT signing key) that would catch a missing override.
- **Affected component:** `k8s/main/conf/appsettings.json`; `k8s/main/deployment-main.yaml`;
  `.github/workflows/build-api.yml`.
- **Likelihood (5-year misuse probability):** 20% — reasoning: this audit could not confirm from repository
  content alone whether the committed value is actually overridden in the live `Secret` today (ASP.NET Core's
  default configuration precedence would support the override working correctly if the `Secret` defines the right
  environment variable names, but this audit has no access to the `Secret`'s actual contents to confirm it does).
  The likelihood is not rated near-zero despite this being "probably fine by design" because: (a) the value has
  never been rotated or replaced with an unmistakable placeholder in over a year of history, which is inconsistent
  with genuinely treating it as sensitive; (b) this repository, per `AUDITS-INSTRUCTIONS.md`'s own publication
  intent, is shared with end users and third parties as evidence of due diligence — a broader audience than
  engineering alone; (c) there is no automated guardrail (fail-fast check, secret-scanning rule, or even an
  obviously-fake placeholder string) preventing this exact scenario from becoming live in a future edit even if it
  is not live today. The likelihood is not rated higher because there is a plausible, standard, and
  code-consistent (per the file's own comment) design under which this is already inert placeholder text overridden
  at deploy time, and this audit found no evidence (only an absence of evidence either way) that it is currently
  exploited or exploitable.
- **Impact:** Critical if the committed value is ever live — full offline decryption of every user's stored
  Algorand mnemonic across the entire user base, requiring only the (non-secret) victim email address, per the
  exact mechanism R-002 already describes. This is why the entry's likelihood is kept moderate rather than the
  impact being downgraded — per `AUDITS-INSTRUCTIONS.md`'s likelihood-estimation discipline, likelihood and impact
  are tracked separately, and a high-impact/uncertain-likelihood risk should not be silently averaged into a
  falsely reassuring blended score.
- **Current mitigations:** The Kubernetes `Secret` + `envFrom` mechanism, *if* it is actually configured to
  override `AesOptions__Key`/`AesOptions__IV` (unconfirmed by this audit), would fully mitigate this risk via
  standard ASP.NET Core configuration precedence (environment variables override `appsettings.json` file values).
- **Recommended further mitigation:** (1) Directly verify, out-of-band against the live production `Secret`/
  container environment, whether `AesOptions__Key`/`AesOptions__IV` are actually set and differ from the committed
  value — the single most important follow-up from the audit that raised this entry. (2) Replace the committed
  value with an unmistakable non-functional placeholder regardless of (1)'s outcome. (3) Add a startup fail-fast
  check (mirroring R-007's pattern) that refuses to start outside Development if `AesOptions.Key`/`IV` matches a
  known-placeholder sentinel or fails basic validation. (4) Consider rotating the production key as a precaution
  if (1) cannot rule out the committed value having been live at some point, with a corresponding re-encryption
  migration path (the versioned-format mechanism from R-002 could be extended with a key-id, similar to the
  existing `AesEncryptionHelper.MakeAesId` filename-namespacing mechanism).
- **Status:** Open.
- **History:**
  - 2026-24-07 — claude-code-ai-review-2: opened at 20%, corresponds to finding G-02 in
    [audit-report-2026-24-07-173793a-claude-code-ai-review-2.md](audit-report-2026-24-07-173793a-claude-code-ai-review-2.md).
    This is the first audit to inspect `k8s/main/conf/*` contents (the first audit, F-13, explicitly deferred this
    check; the 2026-07-24 remediation pass also did not perform it, per R-013's own history). Opened as a new,
    more specific entry rather than folded into R-013, since R-013's original concern was general ("contents
    unverified") while this entry describes a specific, reproducible piece of evidence (an unchanged-for-a-year,
    syntactically-real-looking committed key) with its own severity and remediation path.

## Accepted / unmitigable risks

### R-017 — Total, permanent loss of funds if a user loses both their Google account and any recovery mechanism

- **Description:** This system's self-custody model stores the only copy of a user's encrypted Algorand key
  material in that user's own Google Drive, gated by that user's Google identity. A user who permanently loses
  access to both their Google account (e.g. account termination, forgotten credentials with no recovery path) and
  any independent backup/export of their mnemonic loses access to their funds permanently. Biatec cannot recover
  the key on the user's behalf by design — that is the entire point of the self-custody model documented in
  `CLAUDE.md`'s architecture notes.
- **Why unmitigable given the current architecture:** Any mechanism that let Biatec recover a lost key would
  reintroduce a server-side custody/recovery capability, directly contradicting the self-custody guarantee this
  product is built to provide. This is an inherent tradeoff of the design, not a defect.
- **Status:** Accepted (unmitigable).
- **History:**
  - 2026-23-07 — claude-code-ai-review: opened as accepted/unmitigable; not a defect, users should be clearly
    informed of this tradeoff (e.g. encouraged to export/back up their mnemonic independently) as a UX/disclosure
    matter rather than a code fix.
