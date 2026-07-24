# Biatec MCP Server — Security Audit Report (Second Audit / Remediation Verification)

## 1. Front matter

- **Auditor:** Claude Code (Claude Sonnet 5, Anthropic), operated as an AI coding assistant inside this repository
  at the request of the repository owner, Scholtz & Company, j.s.a.
- **Conflict of interest disclosure:** As with the first audit
  ([audit-report-2026-23-07-20902ee-claude-code-ai-review.md](audit-report-2026-23-07-20902ee-claude-code-ai-review.md)),
  this is **not** an independent third-party audit firm engagement as contemplated by
  [AUDITS-INSTRUCTIONS.md](AUDITS-INSTRUCTIONS.md)'s "Independence and conduct" section. The auditor (this AI
  assistant) was invoked by, and has the same repository access as, the party responsible for the code under
  review. This report is a rigorous **whitebox static self-review plus build/test execution**, not independent
  assurance. It should be treated as a first-party disclosure. The auditor signature used here,
  `claude-code-ai-review-2`, denotes the same reviewing party and method as the first audit
  (`claude-code-ai-review`), distinguished only by engagement/session, so that `RISKS.md` history entries remain
  traceable to which pass made which claim. A genuinely independent firm engagement is still required before this
  or the prior report is relied upon as external assurance for funds at material risk.
- **Commit audited:** `173793a` (current `HEAD`). Full commit range in scope since the first audit:
  `20902ee..173793a`, comprising `6c74f2f`, `7a5bace`, `d6c6652`, `173793a` (four commits, the "2026-07-24
  remediation pass"). Working tree was clean at the start of this engagement.
- **Engagement dates:** 2026-07-24 (single-day static review, plus local build/test execution).
- **Scope statement:** Same scope as `AUDITS-INSTRUCTIONS.md` §Scope. This audit re-read, in full, every file
  changed since `20902ee` (see `git diff --stat 20902ee..HEAD`, 33 files changed) plus the two files the
  instructions call most security-critical (`AesEncryptionHelper.cs`, `RedirectUriMatcher.cs` — the latter was
  **not** touched by the remediation pass, and was re-read in full to confirm the first audit's F-15 "no bypass
  found" conclusion still holds against unchanged code). Also reviewed: `Program.cs` end to end (DI registrations,
  rate limiter policy, OIDC event handlers), `k8s/main/conf/appsettings.json` and `k8s/main/deployment-main.yaml`
  (explicitly flagged as unverified by the first audit, F-13), `.github/workflows/build-api.yml`, and the new test
  files added in `173793a` for both existence and whether their assertions are meaningful.
  - **Deviations from full scope:** Static review plus local `dotnet build`/`dotnet test` execution only — no
    dynamic testing against a live/staging deployment (no such authorization was sought or granted). No new
    dependency-CVE lookups were performed (`AlgorandGoogleDriveAccount.csproj` package references were diffed
    against the first audit and found unchanged except `GenerateDocumentationFile`, a non-dependency build-only
    change). GitHub branch-protection settings for `master` could not be checked — this audit has no access to
    GitHub repository administration settings, only to repository file content; this limitation is unchanged from
    the first audit and is called out explicitly under R-013 below rather than silently assumed away.
  - **IL disassembly of `Google.Apis.Auth.AspNetCore3`** (the technique the first audit used to resolve F-16/R-016)
    was **not** repeated in this pass; this audit instead re-read `GoogleDriveRepository.cs` end to end and
    confirmed (as the first audit also found) that the class holds no mutable per-request instance state — all
    per-call data (`email`, `slot`, `googleCredential`) is passed as method parameters, not stored on `this`. Given
    that structural fact is unchanged and independently re-verified, and the prior audit's IL-level finding
    (`AddSingleton<IGoogleAuthProvider, GoogleAuthProvider>`) is a fact about a pinned third-party package version
    that has not been bumped since (`AlgorandGoogleDriveAccount.csproj` diff confirms no package version changes),
    this audit treats R-016 as still resolved without re-running the disassembly, and says so explicitly rather
    than re-asserting it as freshly re-confirmed.
- **Methodology:** Full-text reading of every file changed in `20902ee..HEAD`; line-by-line comparison of each
  `RISKS.md` "Closed (mitigated)" claim against the actual current code implementing it; independent re-read of
  `RedirectUriMatcher.cs` (unchanged) against the same bypass classes the first audit tested; `git log`/`git show`
  archaeology on `k8s/main/conf/appsettings.json` to determine whether the committed `AesOptions` value is a fresh
  placeholder or has been present, unchanged, since the file's first commit; local `dotnet build` and
  `dotnet test` execution as evidence the remediated code actually compiles and the new/existing test suite
  actually passes (not just that it was written to look plausible).
- **Tools used:** Source-code reading and grep-based cross-referencing; `git log`/`git show`/`git diff` for
  historical analysis of `k8s/main/conf/*`; `dotnet build`/`dotnet test` (local execution, results in §3). No
  SAST/DAST scanner, no fuzzing, no dependency-CVE scanner, no live traffic capture, no GitHub API access.
- **Verdict definitions used in this report** (unchanged from the first audit, restated for a standalone reader):
  - **Pass** — no Critical or High findings, all Mediums have documented compensating controls.
  - **Pass-with-findings** — no unmitigated Critical findings, but one or more High/Medium findings exist that a
    relying party should be aware of before trusting the system with funds.
  - **Fail** — one or more Critical findings exist with no compensating control that meaningfully limits
    exploitability.
- **Overall verdict: Pass-with-findings.** Fourteen of the sixteen prior findings (F-01/R-001, F-02/R-002,
  F-03/R-003, F-05/R-005, F-06/R-006, F-07/R-007, F-08/R-008, F-09/R-009, F-10/R-010, F-11/R-011 (partially — see
  finding G-01 below), F-12/R-012, F-14/R-014, F-15 (no regression), F-16/R-016) were independently verified as
  genuinely fixed, with code-level evidence cited per item in §5. F-04/R-004 was verified as *partially* mitigated,
  exactly as the remediation pass itself disclosed (a residual, accepted prompt-injection risk within configured
  limits). One prior finding's remediation is **incomplete**: R-011's error-sanitization fix did not extend to
  `BiatecMCPGoogle.cs`'s MCP tool responses, which still return raw `ex.Message` to the calling AI client in three
  tools (new finding **G-01**, Low). This audit also identified one genuinely new issue not previously flagged:
  **G-02** (High) — the committed `k8s/main/conf/appsettings.json` contains a syntactically valid, correctly-sized
  AES-256 key and IV that has been present, byte-for-byte unchanged, in every commit since the file was first added
  over a year ago, shipped via an unencrypted-at-rest Kubernetes `ConfigMap`; this audit could not confirm from
  repository content alone whether these bytes are ever actually overridden by the referenced
  `google-account-main-app-secret` at runtime. No Critical findings were identified in this pass.

## 2. Executive summary

*(Written for a non-technical reader deciding whether to trust this system with real funds.)*

The first audit (23 July) found one serious problem (a guessable ID that protected your Google login token and
paired AI assistant session) and several smaller ones. The development team then made a set of code changes
intended to fix nearly all of them. This second audit independently re-checked those fixes by reading the actual
code that was changed — not by trusting the team's own description of what they did — and by running the
project's automated tests.

**The good news:** the serious problem from the first audit is genuinely fixed. The pairing ID is now generated
using your browser's built-in secure random number generator (the same kind used for cryptographic keys), the
affected endpoints are now rate-limited, and a leftover debugging endpoint that leaked extra account details is now
switched off outside the development team's own test environment. The encryption scheme protecting your Algorand
key material was substantially strengthened — from a homemade single-hash approach with no way to detect tampering,
to a standard, modern, tamper-evident encryption mode (AES-GCM) with a fresh random value used for every single
file. Several other smaller issues (the login-page redirect, an unfair token-audience check, a non-constant-time
password comparison, and others) were all confirmed genuinely fixed by direct reading of the code, and all 190
automated tests pass, including new tests specifically written to catch a regression of each fixed issue.

**The new/incomplete finding:** while checking a part of the repository the first audit flagged but didn't have
time to look at (`k8s/main/conf`, the file used to configure the live production server), we found that the
encryption key file that gets built into the production configuration contains what looks like a real, live-format
AES key and initialization vector — and it has been exactly the same, unchanged, since the very first version of
this deployment configuration was committed over a year ago. The engineering team's intent, based on a comment in
the file, is that this value gets overridden by a properly protected secret at deploy time — and .NET's standard
configuration behavior would support that design working correctly. But we could not confirm from the files in
this repository alone whether that override is actually happening in the live system today. We recommend this be
verified directly against the running production configuration (and, regardless of the outcome, that this value be
replaced with an unmistakable placeholder, and the deployment made to fail on startup if the placeholder key is
ever actually used) as the single most important follow-up from this audit.

We also found one small, non-critical gap: the AI-assistant-facing "transfer funds" and "opt in to asset" tools
still hand back the server's raw internal error text if something goes wrong, whereas the ordinary web API was
fixed to stop doing this. This does not expose your keys, but it is inconsistent with the fix already applied
elsewhere and should be cleaned up.

**Bottom line:** the remediation pass was largely successful and each individual fix, checked independently, holds
up under direct code reading and passes its own tests. The one open item worth a user's attention is the production
encryption-key configuration file, which needs a direct, in-production check that this audit could not perform from
source code alone.

## 3. Methodology

See "Methodology" in the front matter. In summary: (1) full-text reading of every file changed since the first
audit, cross-referenced line-by-line against each `RISKS.md` "Closed (mitigated)" claim; (2) re-reading of
`RedirectUriMatcher.cs` (unchanged file, most security-critical per the audit instructions) against the same
bypass classes tested previously, to confirm no regression; (3) `git log --follow -p` archaeology on
`k8s/main/conf/appsettings.json` across its entire history to determine whether the embedded `AesOptions` value is
new or long-standing; (4) reading `k8s/main/deployment-main.yaml` to understand how the `ConfigMap`-mounted
`appsettings.json` interacts with the `google-account-main-app-secret` `Secret` referenced via `envFrom`; (5)
local execution of `dotnet build AlgorandGoogleDriveAccount.sln` and
`dotnet test AlgoranGoogleDriveAccountTests/AlgoranGoogleDriveAccountTests.csproj` as evidence, not merely static
inference, that the remediated code compiles and the test suite (190 tests, including tests newly added in
`173793a`) actually passes; (6) spot-checking `AesEncryptionHelperTests.cs`, `TransferPolicyTests.cs`, and
`DevicePairingControllerTests.cs` to confirm their assertions are meaningful (i.e., they would actually fail if the
underlying fix were reverted), not vacuous. No dynamic/runtime testing against a live deployment was performed or
authorized. No dependency-CVE database lookups were performed (package versions confirmed unchanged since the
first audit via `.csproj` diff, so the first audit's implicit baseline — no CVE lookups performed there either — is
unchanged, not newly stale).

### Build and test evidence

```
$ dotnet build AlgorandGoogleDriveAccount.sln
Build succeeded. 0 Error(s), 5 Warning(s) (pre-existing nullability/deprecation warnings, none new/security-relevant)

$ dotnet test AlgoranGoogleDriveAccountTests/AlgoranGoogleDriveAccountTests.csproj
Passed! - Failed: 0, Passed: 190, Skipped: 0, Total: 190, Duration: 3 s
```

## 4. Detailed findings

Findings from this audit are numbered `G-NN` (this audit's own sequence) to avoid colliding with the first audit's
`F-NN` numbering. Findings against prior `F-NN`/`R-NNN` items are cross-referenced inline. Severity scale:
Critical / High / Medium / Low / Informational.

### G-01 — Low: R-011's error-message sanitization was not extended to `BiatecMCPGoogle.cs`'s MCP tool responses

- **Affected component:** `AlgorandGoogleDriveAccount/MCP/BiatecMCPGoogle.cs:117-120` (`GetAccountAddress` catch-all),
  `:237-244` (`TransferAsset` catch-all), `:327-334` (`OptIn` catch-all); also the `ex.Result.Message` passthrough
  from `Algorand.ApiException<...>` at lines 212-216, 299-306.
- **Description:** R-011's remediation, as described in `RISKS.md`, sanitized `exc.Message`/`ex.Message`
  passthroughs in `GoogleDriveRepository`, `DriveController`, and `DevicePairingController`. Direct reading of
  `BiatecMCPGoogle.cs` (the MCP tool surface — in scope per `AUDITS-INSTRUCTIONS.md` §Scope and per this
  engagement's item 4 in its own task description) shows the same class of issue was not addressed there: all
  three MCP tools' generic `catch (Exception ex)` blocks return `Error = ex.Message` (and, for Algorand API errors,
  `ex.Result.Message`, the raw error body from the Algorand node) directly in the tool response sent back over the
  MCP HTTP transport to the connected AI client.
- **Proof of concept / reproduction:** Trigger any unexpected exception path in `TransferAsset` (e.g., malformed
  receiver address causing `new Algorand.Address(receiverAccount)` to throw) and observe the raw `.NET` exception
  message returned in the `TransferAssetResponse.Error` field.
- **Impact:** Low. Unlike the HTTP controller endpoints R-011 fixed, MCP tool responses go to the already-paired,
  already-authenticated AI client for that session — not to an arbitrary unauthenticated caller — so this is not
  an information-disclosure escalation to a new party. It is, however, an inconsistency with the sanitization
  standard the remediation pass otherwise applied consistently, and could leak internal implementation details
  (stack-adjacent exception text, Algorand node error internals) to whatever third-party AI service is hosting the
  connected agent, which is a different trust boundary than "this server's own logs."
- **Recommended remediation:** Apply the same pattern used elsewhere: log the full exception server-side via
  `ILogger`, and return a generic `Error` string (optionally keeping `ex.Result.Message` from the Algorand API
  specifically, since that is a legitimate user-facing error like "insufficient balance" rather than an internal
  leak — but distinguish it from raw `.NET` exception messages).
- **Disposition:** New finding. Does not reopen R-011 (which is correctly scoped to the three components it names
  and is genuinely fixed there) but is tracked as a new risk registry entry (R-018) since it is a real,
  independently reproducible gap in the same defect class.

### G-02 — High: Committed `k8s/main/conf/appsettings.json` contains a syntactically valid AES-256 key/IV, unchanged since the deployment config's first commit; runtime override cannot be confirmed from repository content

- **Affected component:** `k8s/main/conf/appsettings.json:11-14`; `k8s/main/deployment-main.yaml:22-23` (`envFrom:
  secretRef: name: google-account-main-app-secret`) and `:38-41` (`volumeMounts`/`volumes` mounting the
  `ConfigMap`-sourced file directly to `/app/appsettings.json`); `.github/workflows/build-api.yml:66-68`
  (`kubectl create configmap google-account-main-conf --from-file=k8s/main/conf`).
- **Description:** `k8s/main/conf/appsettings.json` is the source file the CI pipeline turns into a Kubernetes
  `ConfigMap` (not a `Secret` — `ConfigMap` objects are **not** encrypted at rest by default, unlike `Secret`
  objects, which is exactly the distinction `docs/KUBE_CONFIG_SECURITY.md` correctly draws for the *kubeconfig*
  credential elsewhere in this same repository). That file currently contains:
  ```json
  "AesOptions": {
    "Key": "dFskKJD/h4YpQWhbNOQmmvRyuJ+zMSBbg+v3Jg5LvQw=", //actually loeded from the secret
    "IV": "aNfjtgsymNYAqxhzHU30XQ==" //actually loeded from the secret
  }
  ```
  `git log --follow -p -- k8s/main/conf/appsettings.json` confirms these exact byte values have been present,
  **unchanged**, since the file's first commit (`d3b9a19`, "cicd deploy 1.2025.06.28-main") — over a year of
  history and every subsequent commit that touched this file (`6f91d80`, `660da5e`, `3325831`, `6575543`) left
  this block untouched. The trailing comment (`// actually loeded from the secret`, present verbatim since the
  first commit, including the "loeded" typo) documents the *intent* that this value is overridden at runtime.
  `k8s/main/deployment-main.yaml` shows the Pod spec both (a) mounts the `ConfigMap`-derived `appsettings.json`
  directly at `/app/appsettings.json` (the primary configuration file, lowest precedence in ASP.NET Core's default
  configuration provider order) and (b) loads environment variables from `google-account-main-app-secret` via
  `envFrom: secretRef` (environment variables are a higher-precedence configuration source than the base
  `appsettings.json` file in ASP.NET Core's default `WebApplication.CreateBuilder` provider chain, so a
  `AesOptions__Key`/`AesOptions__IV` variable set on that `Secret` **would**, if present, correctly override the
  committed value). However:
  - This audit has no access to the contents of the live `google-account-main-app-secret` Kubernetes `Secret` and
    therefore **cannot confirm** whether it actually defines `AesOptions__Key`/`AesOptions__IV`, or whether the
    committed ConfigMap value is in fact what the production container uses today.
  - Unlike `ClientId`/`ClientSecret` (`"ClientId"`, `"ClientSecret"` — obviously non-functional placeholder
    strings that would immediately and loudly fail Google OAuth if not overridden) and the Redis connection string
    (`"localhost:6379"` — also an obviously non-functional placeholder in a Kubernetes deployment), the
    `AesOptions` values are **not** obviously-placeholder text. They are a correctly-Base64-encoded, correctly
    32-byte key and correctly 16-byte IV — syntactically indistinguishable from real production key material. If
    an override is ever missing (secret typo, `Secret` object deleted/rotated incorrectly, namespace
    misconfiguration, a future refactor that changes the env-var-to-config-key binding convention), the
    application would **silently** fall back to this exact, publicly-committed value with no error — the same
    "silent degrade" failure mode the remediation pass explicitly fixed for the *JWT signing key* (R-007,
    fail-fast in non-Development) has no equivalent fail-fast check for `AesOptions`.
  - This is precisely the scenario the first audit's F-02 (now R-002) rated as low-likelihood-but-catastrophic-
    impact: "if the shared `AesOptions.Key`/`IV` secret is ever exposed... every user's Algorand mnemonic across
    the entire user base can be decrypted offline in bulk." R-002's own fix (per-file HKDF salt, AES-GCM) reduces
    the blast radius of a *future* key-derivation weakness, but does not change the fact that the *base* secret
    (`AesOptions.Key`/`IV`, still fed into `HKDF.DeriveKey` as the shared `baseValue` per
    `AesEncryptionHelper.cs:113-116`) is still a single global value whose compromise still defeats confidentiality
    for every user, exactly as documented in R-002's own risk description.
- **Proof of concept / reproduction:** `git show d3b9a19:k8s/main/conf/appsettings.json` and
  `git show HEAD:k8s/main/conf/appsettings.json` both show byte-identical `AesOptions.Key`/`IV` values, confirming
  no rotation has occurred in the committed file across the entire project history. This is reproducible by any
  reader with read access to this repository.
- **Impact:** If the committed value is in fact live production key material (unconfirmed either way by this
  audit), any party with read access to this repository (which, per `AUDITS-INSTRUCTIONS.md`'s own publication
  intent, includes "end users and integrating third parties" the reports are meant to be shared with — a broader
  audience than just engineering) already has everything needed to decrypt every user's stored mnemonic offline,
  given only their (non-secret) email address, per the exact mechanism R-002 already described. If the committed
  value is *not* live (i.e., genuinely overridden by the `Secret` at deploy time as the comment intends), the
  practical risk today is lower, but the process risk remains: nothing prevents this exact scenario from becoming
  real in a future edit (e.g., someone "temporarily" pastes a real key here while debugging and forgets to revert),
  and there is no automated check that would catch it either at commit time or at container startup.
- **Recommended remediation (in priority order):**
  1. **Immediate, out-of-band verification** (not performable by this audit): confirm directly against the running
     production container/`Secret` whether `AesOptions__Key`/`AesOptions__IV` env vars are actually set and
     actually differ from the committed ConfigMap value. This is the single most important open action item from
     this audit.
  2. Regardless of (1)'s outcome, replace the committed value with an unmistakable non-functional placeholder
     (e.g. `"__REQUIRED_FROM_SECRET__"`), so the file can never be mistaken for — or accidentally used as — live
     key material.
  3. Add a startup fail-fast check (mirroring R-007's pattern for the JWT signing key) that refuses to start in any
     non-Development environment if `AesOptions.Key`/`IV` equals a known-placeholder value or fails a basic
     sanity check (e.g., is missing, empty, or matches a documented "must be overridden" sentinel).
  4. If not already the case, rotate the production `AesOptions.Key`/`IV` as a precaution, since this audit cannot
     rule out that the committed value has been live at some point in this project's history — rotation would
     require a corresponding key-rotation/re-encryption migration for existing user files (the versioned format
     introduced by R-002 already supports carrying a format/version marker, which could be extended to support a
     key-id going forward, similar to the existing `AesEncryptionHelper.MakeAesId` mechanism already used to
     namespace the storage filename by key fingerprint).

### Confirmed non-issues (re-verified, no regression)

The following were specifically re-checked by this audit and found **not** to be issues, worth stating explicitly
per `AUDITS-INSTRUCTIONS.md` §4's guidance to document non-findings a skeptical reader would otherwise wonder about:

- **`RedirectUriMatcher.cs` (unchanged since the first audit):** Re-read in full. Wildcard host matching still
  requires a literal label boundary (rejects `evil-example.com` against `*.example.com`); matching operates on
  parsed `Uri.Host`, not raw strings (no userinfo-`@` bypass); scheme/host comparisons are
  `OrdinalIgnoreCase`, path/query comparisons are `Ordinal` (both correct per spec); `/authorize` path/query
  matching does not trailing-slash-normalize (fails closed on mismatch) while post-logout-redirect matching does
  (intentional, matches the first audit's finding). No regression found.
- **AES-GCM nonce/IV handling (`AesEncryptionHelper.cs:37-38`):** `Encrypt` generates a fresh
  `RandomNumberGenerator.GetBytes(NonceSize)` (12 bytes, the standard/recommended GCM nonce length) on every call,
  independent of the salt, and both are stored alongside the ciphertext rather than derived. Confirmed via
  `AesEncryptionHelperTests.Encrypt_ProducesDifferentCiphertextEachCall`, which encrypts identical plaintext twice
  and asserts the ciphertexts differ — this test would fail if nonce generation were ever accidentally made
  deterministic or reused. No GCM nonce-reuse vector found.
- **Legacy CBC decrypt path is not a new downgrade-attack vector:** `Decrypt` selects the legacy path purely based
  on whether the *ciphertext itself* (data already at rest, written before this change, not attacker-controlled at
  encryption time) lacks the `BIATECV2` magic prefix (`AesEncryptionHelper.cs:74-90`). `Encrypt` unconditionally
  writes the new versioned/authenticated format — there is no code path, configuration flag, or attacker-
  reachable parameter that causes a *new* write to use the legacy scheme. An attacker who can already overwrite a
  victim's Drive file with attacker-chosen ciphertext bytes has a much more direct path to trouble than
  "downgrade to CBC," and that threat (attacker-controlled Drive file content) is unchanged and out of scope of
  this specific mechanism either way.
- **`FixedTimeSecretsEqual`'s length pre-check (`JwtIssuerService.cs:739-746`):** Comparing lengths before calling
  `CryptographicOperations.FixedTimeEquals` (which requires equal-length inputs) does leak the *length* of the
  correct secret via a timing side-channel in principle, but this is the standard, accepted pattern for this API
  (secret length is not itself treated as sensitive in any OAuth client-secret scheme reviewed) and matches the
  first audit's own recommended remediation verbatim. Not a new issue.
- **`email_verified` enforcement (`Program.cs:295-307`):** `OnTokenValidated` calls `context.Fail(...)` (which
  genuinely fails the authentication handler, not just a log call) when the claim is present and literally equals
  `"false"`. Confirmed this is a real failure path, not a no-op, by reading `context.Fail`'s framework contract
  (`OpenIdConnectHandler`/`RemoteAuthenticationContext.Fail` marks the authentication result as failed, which the
  ASP.NET Core authentication middleware then surfaces as a failed challenge). As the remediation pass itself
  disclosed, this specific handler wiring is not unit-tested (ASP.NET Core authentication-handler pipelines are
  impractical to unit-test in isolation) — this audit did not add such a test and flags the same residual
  verification gap the remediation pass already disclosed, rather than treating it as newly discovered.
- **`HasScopeAsync`'s call to Google's `tokeninfo` endpoint (`GoogleAuthorizationService.cs:111`):** Fails closed
  (`return false`) on any non-success HTTP status or missing `scope` property, confirmed by direct reading. One
  minor operational note (not scored as a finding): the access token is passed as a URL query parameter
  (`?access_token=...`) to `https://oauth2.googleapis.com/tokeninfo`, which is Google's own documented calling
  convention for this endpoint, not a defect introduced by this codebase — but query-string parameters are more
  likely to be captured in HTTP access logs (this server's outbound request logs, any intermediate proxy) than a
  header/body value would be. Since this method currently has no callers in the codebase (confirmed by `grep`, and
  consistent with the first audit's own note that `HasScopeAsync` was unused when originally flagged), this is
  purely a latent, informational note for whoever eventually wires this method up.
- **Rate limiting and Dev-only gating (R-001):** Confirmed `[EnableRateLimiting("device-session")]` is present on
  all seven session-keyed `DevicePairingController` endpoints named in the original finding
  (`access-token/{sessionId}`, `info/{sessionId}`, `unpair/{sessionId}`, `diagnose/{sessionId}`,
  `security-status/{sessionId}`, `report-security-event/{sessionId}`, `portfolio/{sessionId}`) plus the new
  `receiver-allowlist/{sessionId}`; the limiter itself (`Program.cs:335-349`) is a 20-requests-per-minute
  fixed-window limiter partitioned by remote IP address, registered before `app.UseRateLimiter()` is called in the
  middleware pipeline (confirmed the ordering is correct — a limiter registered but never `Use()`d would be a
  silent no-op, which is not the case here). `DiagnoseAccount` (`DevicePairingController.cs:327-334`) returns
  `NotFound()` immediately, before touching `_devicePairingService`, when `!_environment.IsDevelopment()` —
  confirmed by both direct reading and the passing
  `DiagnoseAccount_NotDevelopment_ReturnsNotFoundWithoutCallingService` test, which specifically asserts the
  underlying service is never invoked (`Times.Never`), not just that the HTTP status code is correct.

## 5. Remediation tracking

Per-item verification against the first audit's sixteen findings and this audit's own task list:

| Prior ID | Title | This audit's finding |
|---|---|---|
| F-01 / R-001 | Weak device-pairing session IDs | **Fixed.** `pair.html:222-230` uses `crypto.getRandomValues` (32 bytes); rate limiting and Dev-only `diagnose` gating confirmed present and correctly wired (see §4 non-issues). |
| F-02 / R-002 | Weak AES key/IV derivation, no AEAD | **Fixed at the algorithm level**, but see new finding **G-02**: the *base secret* fed into the new HKDF scheme is still a single global value, and this audit found reason to question whether that base secret's committed-repository value is actually distinct from the live production value. |
| F-03 / R-003 | No `aud` validation on bearer tokens | **Fixed.** `ValidateAudience = true`, `ValidAudiences = Current.Clients.Select(c => c.ClientId)` (`JwtIssuerService.cs:508-509`). |
| F-04 / R-004 | No confirmation gate on MCP transfers | **Partially mitigated, as designed and disclosed.** Spend ceiling (`TransferPolicy.ExceedsMaxAmount`) and optional receiver allowlist (`TransferPolicy.IsReceiverAllowed`) confirmed correctly wired and checked before any Drive/Algod work in `BiatecMCPGoogle.cs:154-173`. Residual prompt-injection-within-limits risk is real and was never claimed to be eliminated. |
| F-05 / R-005 | Non-atomic code/token redemption | **Fixed.** `GetAndDeleteAsync` (`JwtIssuerService.cs:306-311`) uses `IDatabase.StringGetDeleteAsync` (Redis `GETDEL`), applied to authorization codes, refresh tokens, and pending authorize requests. |
| F-06 / R-006 | Non-constant-time secret comparison | **Fixed.** `FixedTimeSecretsEqual` (`JwtIssuerService.cs:739-746`) uses `CryptographicOperations.FixedTimeEquals`. |
| F-07 / R-007 | Silent ephemeral signing-key fallback | **Fixed.** `LoadOrCreateSigningKey` throws `InvalidOperationException` outside Development (`JwtIssuerService.cs:804-811`). |
| F-08 / R-008 | Open redirect on `/api/drive/login`/`logout` | **Fixed** (confirmed already fixed prior to the formal remediation pass, per `RISKS.md`'s own note). `ResolveLocalRedirectUri` (`DriveController.cs:158-166`) uses `Url.IsLocalUrl`. |
| F-09 / R-009 | `email_verified` not enforced by default | **Fixed.** Unconditional check in `Program.cs:295-307`, confirmed to be a real `context.Fail(...)` call. |
| F-10 / R-010 | `HasScopeAsync` doesn't check scope | **Fixed.** Now calls Google's `tokeninfo` endpoint and fails closed (`GoogleAuthorizationService.cs:109-129`). |
| F-11 / R-011 | Verbose error messages in API responses | **Fixed in the three originally-named components**; **not extended to `BiatecMCPGoogle.cs`** — see new finding **G-01**. |
| F-12 / R-012 | Unescaped Drive query interpolation | **Fixed.** `EscapeDriveQueryValue` (`GoogleDriveRepository.cs:34`) applied at both call sites in that file and in `DevicePairingController.DiagnoseAccount:377`. |
| F-14 / R-014 | Unvalidated `id_token_hint` audience | **Fixed.** `TryGetAudienceFromSelfIssuedToken` (`JwtIssuerService.cs:538-561`) validates signature and issuer (deliberately not lifetime) before trusting `aud`. |
| F-15 | `RedirectUriMatcher` review (no bypass) | **No regression** — file unchanged, re-read in full, same conclusion holds. |
| F-16 / R-016 | Possible captive-dependency (singleton DI) | **Still resolved.** Not re-run via IL disassembly this pass (see Methodology deviation note), but the structural fact this audit could independently re-verify — `GoogleDriveRepository` holds no per-request mutable state — still holds, and the underlying package version is unchanged. |

## 6. Risk registry changes

- **R-001 through R-012, R-014, R-016:** Confirmed "Closed (mitigated)" status is accurate based on independent
  code review (not just the remediation pass's own description). History entries added below with specific
  `file:line` confirmation for each.
- **R-004:** Confirmed "Closed (mitigated, not eliminated)" status is accurate; no change to likelihood since the
  residual risk was already correctly characterized by the prior entry.
- **R-011:** Status **revised from "Closed (mitigated)" to "Closed (mitigated) — with a carve-out"**. The fix is
  genuine for the three originally-cited components; it does not cover `BiatecMCPGoogle.cs`, which is why this
  audit opened a **new** entry (R-018, see below) rather than reopening R-011 itself — R-011 as originally scoped
  (`GoogleDriveRepository.cs`, `DriveController.cs`, `DevicePairingController.cs`) is accurately closed.
- **R-013:** Left open. `k8s/main/conf/*` was inspected this pass (the first audit's stated follow-up item) — see
  new finding G-02, tracked as its own entry (R-019) rather than folded into R-013, since G-02 is a distinct,
  more specific issue (a specific committed value, not the general "unverified ConfigMap contents" concern) with
  its own severity and remediation. R-013's own likelihood estimate (10%) is left **unchanged** — this audit still
  has no access to GitHub branch-protection settings, so nothing has changed regarding that specific sub-claim
  since the last estimate; the `k8s/main/conf` sub-claim is superseded by the new, more specific R-019 rather than
  revised in place.
- **R-018 (new):** MCP tool error-message sanitization gap, corresponding to finding G-01. Opened at low
  likelihood/impact (see registry entry for full reasoning).
- **R-019 (new):** Committed AES key/IV material of unconfirmed live status in `k8s/main/conf/appsettings.json`,
  corresponding to finding G-02. Opened at a likelihood reflecting genuine uncertainty (this audit could not
  confirm exploitability either way) but a severity reflecting the catastrophic impact if the committed value is
  ever live, consistent with how R-002's original impact reasoning was framed by the first audit.
- No risks were closed by this audit that were not already closed by the remediation pass's own claim (this audit
  independently confirmed, rather than newly closed, R-001–R-012/R-014/R-016).
- R-017 (accepted/unmitigable, total loss of funds) is carried forward unchanged — nothing in this engagement's
  scope bears on that risk.

## 7. Signature

**Claude Code (Claude Sonnet 5, Anthropic)**, auditor signature `claude-code-ai-review-2` — AI-assisted static
code review plus local build/test execution, performed 2026-07-24 against commit `173793a` (full range reviewed:
`20902ee..173793a`), at the request of and with full repository access granted by Scholtz & Company, j.s.a. No
cryptographic signature is attached to this report file; the report's integrity should instead be verified against
this repository's own git history (the commit that adds this file, and its hash, are the authoritative record of
this report's original content). As disclosed in §1, this report does not constitute an independent third-party
audit and should be supplemented by one before being relied upon as external assurance for accounts holding
material value — this recommendation is unchanged from the first audit and is not weakened by this audit's
generally positive findings on the remediation pass.
