# Biatec MCP Server / OIDC Provider — Security Audit Report (Third Audit — Post-Restructure)

## 1. Front matter

- **Auditor:** Claude Code (Claude Sonnet 5, Anthropic), operated as an AI coding assistant inside this repository
  at the request of the repository owner, Scholtz & Company, j.s.a.
- **Conflict of interest disclosure:** As with the first two audits
  ([audit-report-2026-07-23-20902ee-claude-code-ai-review.md](audit-report-2026-07-23-20902ee-claude-code-ai-review.md),
  [audit-report-2026-07-24-173793a-claude-code-ai-review-2.md](audit-report-2026-07-24-173793a-claude-code-ai-review-2.md)),
  this is **not** an independent third-party audit firm engagement as contemplated by
  [AUDITS-INSTRUCTIONS.md](AUDITS-INSTRUCTIONS.md)'s "Independence and conduct" section. The auditor (this AI
  assistant) was invoked by, and has the same repository access as, the party responsible for the code under
  review. This report is a rigorous **whitebox static self-review plus build/test execution**, not independent
  assurance, and should be treated as a first-party disclosure. The auditor signature used here,
  `claude-code-ai-review-3`, denotes the same reviewing party and method as the first two audits, distinguished
  only by engagement/session. A genuinely independent firm engagement is still required before this or any prior
  report is relied upon as external assurance for funds at material risk — this is unchanged from both prior
  reports and is not weakened by this audit's findings.
- **Commit audited:** `69d410c` (current `HEAD`). Full commit range in scope since the second audit: `173793a..HEAD`
  (169 files changed, +17,591/-3,453 lines — this range includes a **full repository restructure**: the single
  `AlgorandGoogleDriveAccount` project was split into three projects, `BiatecSelfCustodyCore` (shared self-custody
  library), `BiatecMCP` (MCP server + device pairing), and `BiatecOIDC` (OIDC/JWT issuer + wallet API), plus
  Microsoft Entra ID / OneDrive support as a second storage provider, a stage environment, a manually-gated
  production-promotion pipeline, and — most recently — a multi-seed vault with on-chain rekey support, an AES
  key-ring rotation mechanism, and cached/refreshable provider access tokens embedded in issued OIDC tokens).
  Working tree was clean at the start of this engagement (`git status --short` empty).
- **Engagement dates:** 2026-08-01 (single-day static review, plus local build/test execution).
- **Scope statement:** Same scope as `AUDITS-INSTRUCTIONS.md` §Scope, mapped onto the new project layout per
  `CLAUDE.md`'s "Solution layout": `BiatecSelfCustodyCore/Helper/AesEncryptionHelper.cs` (still the single most
  security-critical file, now joined by `AesKeyRingResolver.cs` and `EncryptedKeyRingFileStore.cs`, which carry
  equivalent weight since they gate which key material `AesEncryptionHelper` is even called with),
  `BiatecOIDC/Helper/RedirectUriMatcher.cs`, `BiatecSelfCustodyCore/Repository/CloudAccountRepository.cs` (the
  successor to `GoogleDriveRepository`), `BiatecSelfCustodyCore/Providers/*` (both storage backends),
  `BiatecOIDC/BusinessLogic/JwtIssuerService.cs` + `ProviderAccessTokenProtector.cs`, `BiatecOIDC/Controllers/*`,
  `BiatecMCP/Controllers/DevicePairingController.cs`, `BiatecMCP/MCP/BiatecMCPGoogle.cs`, `k8s/main/conf-*/*.json`
  and `k8s/main/deployment-*.yaml`, and `.github/workflows/deploy-stage.yml` /
  `.github/workflows/promote-production.yml` (the CI/CD pipeline, now split into two workflows — see finding
  P-04 below). This audit specifically prioritized the code that is genuinely new or materially changed since the
  last audit rather than re-reading every unchanged file end to end: the multi-seed vault / on-chain rekey feature,
  the AES key-ring rotation mechanism, the provider-access-token caching/refresh mechanism, the cross-cloud vault
  backup flow, and the scope-handling changes to `/authorize`, none of which existed at the time of the second
  audit.
  - **Deviations from full scope:** Static review plus local `dotnet build`/`dotnet test` execution only — no
    dynamic testing against a live/staging deployment (no such authorization was sought or granted). No formal
    dependency-CVE database query was run, but the local build itself surfaced a live advisory (`NU1902`, see
    finding P-03) that this audit did follow up on directly from the build output — this is a stronger baseline
    than the first two audits' "no dependency-CVE lookups performed" note. GitHub branch-protection settings for
    `master` remain unverifiable from repository content alone (same limitation as both prior audits). The
    `Microsoft.Graph`/Entra ID integration's Graph REST calls were reviewed from this codebase's side only — no
    review of Microsoft's own service-side behavior was possible or attempted, consistent with how the first
    audit treated Google's APIs.
- **Methodology:** Full-text reading of every genuinely new or materially-changed security-relevant file in
  `173793a..HEAD` (see file:line citations throughout §4); line-by-line comparison of each `RISKS.md` "Closed"
  entry's underlying code against its current location in the restructured codebase, to confirm no regression
  survived the project split; independent re-read of `RedirectUriMatcher.cs` (moved to `BiatecOIDC/Helper/`,
  otherwise byte-for-byte unchanged) against the same bypass classes both prior audits tested; `git log --follow -p`
  archaeology on both `k8s/main/conf-mcp/appsettings.json` and the new `k8s/main/conf-oidc/appsettings.json` to
  determine whether the `AesOptions`/`ProviderTokenProtection` values are fresh or long-standing; reading
  `k8s/main/deployment-mcp.yaml`/`deployment-oidc.yaml` and both GitHub Actions workflows to understand the new
  stage/production deployment topology; local execution of `dotnet build Biatec.slnx` and
  `dotnet test BiatecMCPTests/BiatecMCPTests.csproj` / `dotnet test BiatecOIDCTests/BiatecOIDCTests.csproj` as
  evidence the current code actually compiles and the test suite actually passes.
- **Tools used:** Source-code reading and grep-based cross-referencing; `git log`/`git show`/`git diff` for
  historical analysis; `dotnet build`/`dotnet test` (local execution, results in §3, including the `NU1902`
  package-advisory warnings surfaced by the build itself). No SAST/DAST scanner, no fuzzing, no live traffic
  capture, no GitHub API access.
- **Verdict definitions used in this report** (unchanged from both prior audits, restated for a standalone reader):
  - **Pass** — no Critical or High findings, all Mediums have documented compensating controls.
  - **Pass-with-findings** — no unmitigated Critical findings, but one or more High/Medium findings exist that a
    relying party should be aware of before trusting the system with funds.
  - **Fail** — one or more Critical findings exist with no compensating control that meaningfully limits
    exploitability.
- **Overall verdict: Pass-with-findings.** No Critical findings. One **new High finding (H-01)** was identified: the
  new cross-cloud vault-backup flow (`VaultBackupController`/`VaultBackupService`) binds its OAuth `state`
  parameter to the *initiating Biatec account*, not to the browser session completing the consent screen — this is
  the classic "login CSRF" gap, and in this specific flow it lets an attacker lure an unrelated victim (who is
  merely logged into their own Google/Microsoft account in a browser) into having *the attacker's* encrypted vault
  written into *the victim's* cloud storage folder, under the exact filename the victim's own vault uses — an
  integrity/denial-of-service attack against a victim who was never a party to the attacker's Biatec session. Two
  Medium findings (M-01: no optimistic-concurrency/locking on the seed-vault file, so concurrent writes can
  silently drop a just-created seed or a primary-seed switch; M-02: a moderate-severity supply-chain advisory,
  `GHSA-59j7-ghrg-fj52`, against the pinned `Microsoft.IdentityModel.*`/`System.IdentityModel.Tokens.Jwt` 5.5.0
  packages that both the JWT issuer and the OIDC client handlers depend on). Two carried-forward Low findings
  (R-018, R-019) were re-verified: R-018 (raw MCP tool error messages) is **still open, unchanged**. R-019
  (committed AES key/IV of unconfirmed live status) is **still substantively open** — the byte-identical key/IV
  first flagged over a year ago remains committed unchanged, though the surrounding `KeyId`/`ActiveKeyId` fields
  were relabeled to the literal string `"placeholder"` as part of the unrelated key-ring-rotation refactor (a
  cosmetic improvement, not the fail-fast guardrail the second audit recommended) — and this audit found the
  identical concern now **also applies to the new `ProviderTokenProtection` key ring**, whose committed placeholder
  key protects every relying party's cached Google/Microsoft access *and refresh* tokens (see finding P-01, an
  extension of R-019). Positively, this audit found a genuine, unprompted improvement to R-013's original CI/CD
  concern: production is no longer deployed automatically on every push to `master` — see finding P-04. All 377
  automated tests (107 + 270 across the two new test projects) pass.

## 2. Executive summary

*(Written for a non-technical reader deciding whether to trust this system with real funds.)*

Since the last audit, the development team did a major piece of engineering: they split one project into three,
added support for signing in with a Microsoft account (not just Google), and built several substantial new
features — the ability to generate a backup key ("seed") and formally switch your account over to it on the
blockchain (useful if you ever suspect your original key was compromised), a mechanism to rotate the master
encryption key protecting everyone's stored keys without needing to re-encrypt everything at once, and a feature
that lets your connected app keep working across long sessions without you having to repeatedly log back in to
your Google or Microsoft account. This audit focused on reviewing all of that new work line by line, alongside
re-confirming nothing broke in the process of restructuring the whole codebase.

**The most important thing we found:** a new feature that lets you back up your encrypted key file to a second
cloud provider (e.g., copy it from Google Drive to OneDrive as insurance) has a design flaw in how it identifies
"whose backup this is" during the step where you grant that second provider permission. A malicious user of this
system could craft a link that — if an entirely unrelated person clicks it while already logged into their own
Google or Microsoft account and clicks through the consent screen — causes *the attacker's* encrypted key file to
be written into *that unrelated person's* cloud storage, under the exact same file name the victim's own key file
uses. Depending on which cloud provider is involved, this could directly overwrite and corrupt the victim's own
stored key file, effectively locking them out of their account until it's manually sorted out. This does not let
the attacker steal the victim's funds or key material — it's a "vandalism" attack, not a theft — but it is a real,
concrete way an outside party who is never even a customer relationship with the victim could damage the victim's
account, purely by getting them to click a link and click through an OAuth consent screen. We recommend this be
fixed before the backup feature is enabled for real users, by binding the OAuth flow to a value the browser itself
proves it received (a standard anti-CSRF cookie), not just to who started the process on the server.

**Two smaller issues:** first, when two requests to change your account's keys happen to overlap in time (for
example, creating a new backup key and then immediately switching to it, in quick succession), there's no
protection against one of those changes getting silently lost — the system doesn't yet lock the file to prevent
this. Second, a routine build of the project surfaced a known, publicly-disclosed vulnerability in one of the
open-source libraries this system depends on (used for handling security tokens); it should be updated.

**The good news:** every issue from the two previous audits was independently re-checked against the current code
and found either still fixed or, in the two cases that were already flagged as open (raw error messages sent to
connected AI assistants, and a should-be-inert-but-real-looking encryption key committed in the deployment
configuration), unchanged in status — nothing regressed during the restructuring. We also found that the team made
an unprompted, meaningful improvement to how code reaches production: previously, every change pushed to the main
branch went live automatically; now, pushes only reach a separate staging environment, and a human must explicitly
and manually approve promoting a specific, already-tested version to production. All 377 automated tests pass.

**Bottom line:** the core encryption and identity-verification design remains sound and the team continues to
respond well to prior findings. The one issue that needs attention before real users rely on the new backup
feature is the cross-provider link-forgery weakness described above; everything else identified here is a smaller,
well-contained gap with a clear fix.

## 3. Methodology

See "Methodology" in the front matter for the full list. In summary: (1) full-text reading of every new/materially
changed security-relevant file since the second audit; (2) line-by-line remapping of every prior `RISKS.md`
"Closed" entry onto its new location in the restructured codebase; (3) manual trust-boundary tracing of the new
vault-backup OAuth flow (`VaultBackupController`/`VaultBackupService`/`ICloudStorageProvider.BuildAuthorizationUrl`/
`ExchangeAuthorizationCodeAsync`) against the standard OAuth "login CSRF" bypass class; (4) `git log --follow -p`
archaeology on both production `appsettings.json` files; (5) local `dotnet build Biatec.slnx` and
`dotnet test` execution for both new test projects.

### Build and test evidence

```
$ dotnet build Biatec.slnx
Build succeeded. 0 Error(s), 601 Warning(s) (pre-existing style/nullability/CA-rule warnings across the newly-split
test projects, none new/security-relevant, plus 6 NU1902 package-advisory warnings — see finding M-02)

$ dotnet test BiatecMCPTests/BiatecMCPTests.csproj
Passed! - Failed: 0, Passed: 107, Skipped: 0, Total: 107, Duration: 4 s

$ dotnet test BiatecOIDCTests/BiatecOIDCTests.csproj
Passed! - Failed: 0, Passed: 270, Skipped: 0, Total: 270, Duration: 4 s
```

## 4. Detailed findings

Findings from this audit are numbered `H-NN`/`M-NN`/`P-NN` (High/Medium/informational-or-positive, this audit's own
sequence) to avoid colliding with prior `F-NN`/`G-NN` numbering. Severity scale: Critical / High / Medium / Low /
Informational.

### H-01 — High: Vault-backup OAuth flow has no CSRF binding to the browser session completing consent; a victim's cloud storage can be made to receive an attacker's encrypted vault under the victim's own account-file name

- **Affected component:** `BiatecOIDC/Controllers/VaultBackupController.cs:80-103` (`Authorize`/`Callback`);
  `BiatecOIDC/BusinessLogic/VaultBackupService.cs:35-89` (`StartAsync`/`HandleCallbackAsync`); `:91-127`
  (`CompleteAsync`); `BiatecSelfCustodyCore/Providers/MicrosoftCloudStorageProvider.cs:196-205` (`UploadAsync` —
  HTTP `PUT` to a fixed item path, which overwrites existing content in place);
  `BiatecSelfCustodyCore/Providers/GoogleCloudStorageProvider.cs:240-269` (`UploadAsync` — `Files.Create`, which
  can create a same-named duplicate in the same folder rather than overwrite, but still corrupts subsequent reads
  — see Impact).
- **Description:** The vault-backup feature (`CLAUDE.md`'s "Cross-cloud vault backup") is a three-step flow: (1)
  `POST /wallet/backup/start` (a bearer-token API call, requiring the `sign` claim) generates an unguessable
  `linkId` and records `PendingVaultBackup(email, targetProvider)` in Redis, keyed by that `linkId`; (2) the caller
  is expected to open `GET /wallet/backup/authorize?linkId=...` in a browser, which redirects to the target
  provider's own OAuth consent screen with `state=linkId`; (3) after the user consents, the provider redirects back
  to `GET /wallet/backup/callback?code=...&state=...`, and `state` (the `linkId`) is used directly to look up which
  pending backup this authorization belongs to and to cache the resulting access token
  (`VaultBackupService.cs:85-88`); (4) `POST /wallet/backup/complete` (bearer-token API call, again requiring
  `sign`) spends the cached access token to copy the caller's own encrypted vault file into the target provider
  using that token.

  The `linkId`/`state` value is unguessable (a `Guid.NewGuid()`-derived 32-hex-character string,
  `VaultBackupService.cs:129`) and is correctly single-use (`HandleCallbackAsync` moves it from a `pending:` Redis
  key to a `linked:` key on success; `CompleteAsync` does an atomic `GETDEL` so it can never be spent twice —
  `VaultBackupService.cs:87,96`). What it does **not** do is what a `state` parameter is conventionally relied on
  for in OAuth: proving that the browser completing the consent screen is the same party — or acting on behalf of
  the same party — that initiated step (1). Steps 2 and 3 (`Authorize`/`Callback`) are `[AllowAnonymous]` and carry
  no session cookie, CSRF token, or any other binding back to whoever called `Start`. The only identity information
  the flow retains from step (1) is `pending.Email` — whichever Biatec account happened to call `Start`, which is
  entirely attacker-controlled if the attacker is the one who calls it.

  Concretely: an attacker who holds *any* valid, `sign`-scoped Biatec access token (i.e., is a legitimate user of
  this system for their own account — a very low bar, since anyone can sign in with their own Google/Microsoft
  account) can call `POST /wallet/backup/start` themselves, obtain a `linkId`, and send an unrelated victim a link
  of the form `https://oidc.biatec.io/wallet/backup/authorize?linkId=<attacker's linkId>`. If the victim — who has
  no Biatec session of their own in play, and no reason to think this link concerns anyone but themselves — is
  merely logged into their own Google or Microsoft account in their browser and clicks through the resulting
  consent screen (which will look like an ordinary "Biatec wants access to your Drive/OneDrive app folder"
  prompt), the victim's own provider access token gets cached under the **attacker's** `linkId`
  (`LinkedVaultBackup(pending.Email = attacker's email, TargetProvider, TargetProviderAccessToken = victim's real
  token)`, `VaultBackupService.cs:85`). The attacker then calls `POST /wallet/backup/complete` with their own
  bearer token; `CompleteAsync`'s only integrity check is `linked.Email == email` (`VaultBackupService.cs:103`),
  which trivially passes because `pending.Email` was the attacker's own email from the start. The call proceeds to
  fetch the **attacker's own** encrypted vault bytes (`_accountRepository.GetEncryptedVaultForBackupAsync`) and
  upload them — using the **victim's** captured access token — into the **victim's** cloud storage, under the
  fixed account-file name every user's vault uses (`Configuration.StorageFileName`, e.g.
  `AVMAccount.<aesid>.dat`, the same name regardless of whose vault it is).
- **Proof of concept / reproduction:**
  1. As attacker (holding a `sign`-scoped Biatec token for their own account), call `POST /wallet/backup/start`
     with `{"targetProvider": "Microsoft"}` (or `"Google"`); record the returned `authorizeUrl`/`linkId`.
  2. Send the victim the `authorizeUrl` (or the raw `GET /wallet/backup/authorize?linkId=...` link) through any
     out-of-band channel. The victim, signed into their own Microsoft/Google account in-browser, opens it and
     completes the resulting consent screen (nothing about it visually indicates whose backup this is for).
  3. As attacker, call `POST /wallet/backup/complete` with `{"linkId": "<the same linkId>"}`.
  4. Observe (from the victim's side) that a file matching the account-file naming pattern now exists in the
     victim's OneDrive app folder / Google Drive `Biatec` folder, containing the **attacker's** encrypted vault
     ciphertext — not the victim's own.
- **Impact:** High. This is an integrity/availability attack, not a confidentiality or fund-theft one — the
  attacker never obtains the victim's key material, and the victim's provider access token is spent exactly once,
  for exactly this write, and never cached or reused by the attacker (`VaultBackupService.cs`'s `LinkedVaultBackup`
  is a one-shot `GETDEL` record). But the consequence for the victim is serious:
  - On **Microsoft/OneDrive**, `UploadAsync` is an HTTP `PUT` to a fixed item path
    (`/me/drive/special/approot:/{fileName}:/content`), which is a direct, in-place overwrite of whatever content
    already exists at that path. If the victim already has a Biatec self-custody vault (i.e., they are themselves
    an existing Biatec user, or the attacker chooses to target the file name pre-emptively before the victim ever
    signs up), their own encrypted vault file is silently destroyed and replaced with the attacker's ciphertext.
    The next time the victim (or Biatec, on their behalf) tries to decrypt it, `AesEncryptionHelper.DecryptV2`'s
    AES-GCM authentication tag check will fail (the ciphertext was HKDF-derived against the *attacker's* email,
    not the victim's) — surfaced to the victim as "Unable to load the account. Please try re-pairing the device."
    This is effectively a remote, unauthenticated denial-of-service against a specific victim's self-custody vault,
    requiring only that the victim can be persuaded to click one link and one "Allow" button, both of which look
    completely ordinary.
  - On **Google Drive**, `UploadAsync` uses `Files.Create`, which does not overwrite an existing same-named file
    outright, but Drive permits multiple files with identical names in the same folder — the read path
    (`FindFileAsync`, `GoogleCloudStorageProvider.cs:332-339`) resolves by name via `.FirstOrDefault()` with no
    tie-breaking guarantee, so once a second, attacker-authored file with the same name exists, which one a future
    read actually returns is not the victim's to control. This is a less deterministic but still real corruption
    vector on Google.
  - Because the attack requires no privileged access to the victim's account and no prior relationship between
    attacker and victim beyond the victim clicking a link, this is reachable by any Biatec user against any target
    who can be lured into completing an OAuth consent screen — a realistic bar for a social-engineering campaign,
    and the entire flow looks legitimate to the victim (a real Biatec-branded backup consent screen for a real
    OAuth scope, just for the wrong "whose backup is this" party).
- **Recommended remediation:**
  1. Bind the flow to the completing browser, not just to whoever called `Start`. The standard fix is a
     random anti-CSRF value set as an `HttpOnly`, `SameSite=Lax` (or `Strict`) cookie by `Authorize` before
     redirecting to the target provider, echoed back by the target provider as (or alongside) `state`, and checked
     against the cookie in `Callback` before proceeding — this is exactly the pattern already used for the primary
     Google/Microsoft sign-in flows via the framework's own OpenIdConnect handler (which sets a correlation cookie
     for this exact reason); `VaultBackupController` deliberately doesn't reuse that handler (per its own
     documented design rationale — reusing it would overwrite the caller's real `biatec_idp` session claim) but
     needs an equivalent purpose-built anti-CSRF mechanism of its own, not none at all.
  2. As defense in depth, consider requiring the browser completing `Authorize`/`Callback` to also present
     evidence of being the same principal who called `Start` — e.g., a short-lived, `HttpOnly` cookie set by
     `Start` itself (which does have the caller's bearer token) and checked in `Callback` — rather than relying
     solely on possession of the `linkId` URL.
  3. Independently of (1)/(2), consider namespacing the backup upload's destination file name by the *source*
     account's identity (e.g., include a hash of the source email/vault in the backup file name) rather than
     reusing the exact primary-vault file-name template — this would prevent a successful forged backup from ever
     colliding with a legitimate file already present in the victim's folder, containing the damage to "an unwanted
     extra file appeared" rather than "the victim's own vault was overwritten," even if (1)/(2) are ever bypassed
     or misconfigured in the future.
- **Disposition:** New finding. This feature is new since the second audit (did not exist at `173793a`).

### M-01 — Medium: No optimistic-concurrency control on the seed-vault file; concurrent writes can silently lose a seed creation or a primary-seed switch

- **Affected component:** `BiatecSelfCustodyCore/Repository/CloudAccountRepository.cs:110-182` (`CreateSeedAsync`,
  `SwitchPrimarySeedAsync`); `BiatecSelfCustodyCore/Helper/EncryptedKeyRingFileStore.cs:76-88` (`SaveAsync` —
  unconditional upload, no ETag/If-Match precondition); `GoogleCloudStorageProvider.cs:240-269` /
  `MicrosoftCloudStorageProvider.cs:196-205` (`UploadAsync` — neither passes nor checks any conditional-write
  header).
- **Description:** Every seed-vault mutation (`CreateSeedAsync`, `SwitchPrimarySeedAsync`, and the legacy-format
  migration path inside `LoadVaultOrEmptyAsync`) follows the same read-modify-write pattern: download and decrypt
  the current vault, mutate the in-memory `SeedVault` object, re-encrypt, and upload — with no compare-and-swap,
  ETag precondition, distributed lock, or any other mechanism to detect that the file changed between the read and
  the write. Two concurrent mutations against the same account (e.g., a client that double-submits
  `POST /wallet/seeds` due to a network retry, or a legitimate `POST /wallet/seeds` racing a
  `PUT /wallet/seeds/primary` issued moments later by the same client) can both read the same starting state,
  and whichever finishes its `UploadAsync` last silently wins, discarding the other's change with no error surfaced
  to either caller.
- **Impact:** Medium. This is a correctness/availability issue rather than a confidentiality one — no seed's
  mnemonic is ever exposed, and existing seeds are never *deleted* by this race (both concurrent writers still
  start from a vault containing all existing seeds; only the very last mutation's *addition* survives). But the
  specific sequences this can lose are exactly the ones the multi-seed/rekey feature exists to make safe:
  - A `CreateSeedAsync` racing another `CreateSeedAsync` (e.g., a double-submitted "generate a new seed" request)
    can result in one of the two newly-generated seeds being silently discarded — the caller believes it recorded
    a new recovery seed (and may have already communicated its address elsewhere, e.g. begun drafting an on-chain
    rekey transaction to it) when in fact it was never persisted.
  - A `SwitchPrimarySeedAsync` racing a `CreateSeedAsync` can result in the primary-switch being silently lost if
    the create's write lands last (the create's snapshot was read before the switch, so its write carries the
    pre-switch `IsPrimary` flags forward) — leaving Biatec signing with the old primary key even though the caller
    believed the switch succeeded (`SwitchPrimarySeedAsync` returns `200 OK` before this race could be detected).
    Per `CLAUDE.md`'s own stated invariant ("switching primary before \[on-chain rekey confirmation\] would make
    Biatec sign with a key the account no longer recognizes"), the inverse failure mode here — believing the
    switch happened when it silently didn't — is just as capable of producing signed-but-rejected transactions,
    now for a less obvious reason (a race, not caller error).
- **Recommended remediation:** Add an optimistic-concurrency check to the vault read-modify-write cycle — both
  `ICloudStorageProvider` implementations' backing stores support conditional writes (Google Drive: `Files.Update`
  with `ifMatch`-equivalent revision checks are more limited than Graph, but a read-the-file's-current-hash-before-
  write-and-compare pattern is implementable at this layer regardless of provider-native support; Microsoft
  Graph natively supports `If-Match` against an item's `eTag` on `PUT`). At minimum, have `SaveAsync` detect a
  changed underlying file since the paired `LoadAsync` and fail the mutation with a caller-visible, retryable error
  rather than silently overwriting — this alone would surface the race as a 409-style error the caller can retry
  against the now-current state, rather than a silent loss.
- **Disposition:** New finding — the multi-seed vault (and its read-modify-write pattern) is new since the second
  audit.

### M-02 — Medium: Pinned `Microsoft.IdentityModel.*` / `System.IdentityModel.Tokens.Jwt` 5.5.0 packages carry a known, moderate-severity advisory (GHSA-59j7-ghrg-fj52)

- **Affected component:** `BiatecMCP.csproj`, `BiatecSelfCustodyCore.csproj`, `BiatecMCPTests.csproj` (transitively
  also `BiatecOIDC`/`BiatecOIDCTests`, which depend on `BiatecSelfCustodyCore`) — all reference
  `System.IdentityModel.Tokens.Jwt`/`Microsoft.IdentityModel.JsonWebTokens` 5.5.0, the package family used
  throughout `JwtIssuerService.cs` for signing/validating every access token, ID token, and refresh token this
  system issues, and by both apps' Google/Microsoft `AddOpenIdConnect` handlers for validating the identity
  provider's own tokens.
- **Description:** `dotnet build Biatec.slnx` emits `NU1902` warnings (NuGet's built-in, offline vulnerability-
  database check) identifying `GHSA-59j7-ghrg-fj52` against the installed 5.5.0 versions of both packages, rated
  "moderate severity" by the advisory itself. This is not a new defect introduced by the restructure — the same
  package version was already pinned at the time of the first audit, per that audit's own package-reference diff
  — but neither prior audit's methodology surfaced it: the first audit performed no dependency-CVE lookups at all,
  and the second audit's methodology note explicitly says the same ("no dependency-CVE database lookups were
  performed"). This audit's local build run happened to surface it directly via NuGet's own advisory check, which
  this report is treating as new evidence rather than a newly-introduced issue.
- **Impact:** Medium (unconfirmed exploitability against this codebase's specific usage pattern — this audit did
  not fetch the advisory's full text to assess whether the vulnerable code path is one this codebase actually
  exercises, which would require network access this audit did not use; NuGet's own severity rating is taken at
  face value). Given the package's role (signing/validating every OIDC token this system issues and consumes),
  any vulnerability in it sits directly on the trust boundary between "a token is cryptographically valid" and
  "a token is accepted" — the single most security-sensitive function in `BiatecOIDC`.
- **Recommended remediation:** Upgrade `System.IdentityModel.Tokens.Jwt`/`Microsoft.IdentityModel.JsonWebTokens`
  (and any other package in the same release train, e.g. `Microsoft.IdentityModel.Tokens`) to a version past the
  one the advisory applies to, and re-run the full test suite to confirm no breaking API changes. Add a
  dependency-vulnerability scan (`dotnet list package --vulnerable`, or the equivalent in CI) as a recurring check
  rather than relying on an auditor happening to notice `NU1902` output, since neither prior audit did.
- **Disposition:** New finding (first audit to actually surface it), though the underlying package version
  predates this audit's scope.

### Confirmed carried-forward findings (re-verified this audit)

- **R-018 — MCP tool responses still return raw internal exception messages.** Re-confirmed unchanged.
  `BiatecMCP/MCP/BiatecMCPGoogle.cs:99` (`GetAccountAddress`), `:188,215` (`TransferAsset`), `:272,299`
  (`OptIn`) all still return `ex.Message`/`ex.Result.Message` directly in the MCP tool response. No remediation
  attempt visible in the diff since the second audit; status and likelihood unchanged from R-018's existing entry.
- **R-019 — Committed `AesOptions` key/IV of unconfirmed live status.** Substantively unchanged. The exact same
  `Key`/`IV` byte values (`dFskKJD/h4YpQWhbNOQmmvRyuJ+zMSBbg+v3Jg5LvQw=` /
  `aNfjtgsymNYAqxhzHU30XQ==`) flagged by the second audit are still present, byte-for-byte, in both
  `k8s/main/conf-mcp/appsettings.json` and the new `k8s/main/conf-oidc/appsettings.json` — confirmed via
  `git log --follow -p`, unchanged since commit `d3b9a19` (over a year of history) straight through the entire
  restructure. The unrelated AES key-ring-rotation refactor (see `CLAUDE.md`'s "AES key-ring rotation" section)
  did relabel the surrounding fields — `"ActiveKeyId": "placeholder"` and `"KeyId": "placeholder"` — which is a
  genuine, if purely cosmetic, improvement toward the second audit's recommendation #2 ("replace the committed
  value with an unmistakable non-functional placeholder"): a reader (or a future automated secret-scanning rule)
  now has a literal string to grep for. However: (a) the actual `Key`/`IV` bytes — the part that matters
  cryptographically — are unchanged and still syntactically indistinguishable from real key material; (b) the
  second audit's recommendation #3 (a startup fail-fast check that rejects a known-placeholder sentinel, mirroring
  `JwtIssuerService.LoadOrCreateSigningKey`'s pattern) was **not** implemented — `AesKeyRingResolver.GetActiveKey`
  (called from both `CloudAccountRepository`'s and `ProviderAccessTokenProtector`'s constructors outside
  Development) only validates that the configured key is *syntactically* well-formed (correct base64, correct
  length) and that `ActiveKeyId` resolves to a `Keys[]` entry — it does not check whether the resolved value equals
  this specific known-committed placeholder, so a deployment that failed to override it would start up
  successfully and silently serve production traffic under the committed key, exactly as before. This audit
  extends R-019's scope (see new finding P-01 below) rather than reopening it, since the underlying evidence and
  reasoning are unchanged, just now applicable to a second key ring too.

### Positive/informational notes (new since the second audit)

- **P-01 (extends R-019) — The same "committed, syntactically-valid, unconfirmed-live key material" pattern now
  also applies to `ProviderTokenProtection`.** `k8s/main/conf-oidc/appsettings.json` contains a second key ring,
  `ProviderTokenProtection` (`ActiveKeyId`/`KeyId`: `"placeholder"`; `Key`: `g46fY8Nnr77edXDqCKP+d92nm8roYITklIVy4mGFE2w=`;
  `IV`: `T0Oc4SEMxUfljFeJEj8tfQ==`), which protects the `provider_token`/`provider_refresh_token` claims
  (`ProviderAccessTokenProtector.ClaimType`/`RefreshClaimType`) embedded in every access and refresh token
  `BiatecOIDC` issues to every relying party — per `CLAUDE.md`'s "Provider access token caching" note, this is
  what lets a relying party read/write a user's Drive/OneDrive without ever holding the user's own Google/
  Microsoft token directly. If this specific committed value were ever live in production (this audit, like the
  second audit's equivalent finding for `AesOptions`, has no way to confirm this from repository content alone),
  the impact would arguably exceed R-019's original `AesOptions` scenario: decrypting a captured Biatec token's
  `provider_refresh_token` claim would hand an attacker a **live, renewable** Google/Microsoft credential for that
  user's cloud storage — not just read access to one already-encrypted vault file, but ongoing read/write/delete
  access to the user's entire app-scoped Drive/OneDrive folder for as long as the refresh token remains valid
  (`RefreshAccessTokenAsync` on both providers). This audit recommends `RISKS.md`'s R-019 entry be broadened (or a
  paired entry opened) to explicitly cover `ProviderTokenProtection`, and that the same remediation priority order
  the second audit recommended for `AesOptions` (out-of-band verification, unmistakable placeholder — already
  half-done via the `KeyId` relabel — startup fail-fast against the known sentinel value, and precautionary
  rotation) be applied to both key rings together, since they are deployed via the same mechanism
  (`google-account-main-app-secret` `envFrom`) and therefore share the same verification gap.
- **P-02 — `RedirectUriMatcher.cs` (moved, not materially changed): re-read in full at its new location
  (`BiatecOIDC/Helper/RedirectUriMatcher.cs`), confirmed byte-for-byte equivalent to the version both prior audits
  reviewed. No regression.
- **P-03 — AES-GCM/HKDF algorithm-level design (`AesEncryptionHelper.cs`, `AesKeyRingResolver.cs`,
  `EncryptedKeyRingFileStore.cs`): re-read in full given the key-ring rotation is new since the last audit.** The
  rotation design is sound: each key generation's ciphertext lives under a distinct, deterministically-derived
  file name (`AesEncryptionHelper.MakeAesId`), so `EncryptedKeyRingFileStore.LoadAsync` never needs to blind-guess
  which key decrypts an existing file (which would be unsafe against the legacy unauthenticated-CBC format still
  supported for very old files) — it tries the active generation's exact file name, then each historical
  generation's exact file name in turn, and only ever decrypts a file with the exact key generation known (by its
  file name) to have produced it. `ProviderAccessTokenProtector.Unprotect`'s different approach (blind trial-
  decryption across all configured generations) is safe for the different reason it documents: it only ever writes
  the authenticated AES-GCM format, so a wrong key deterministically fails the auth-tag check rather than risking
  silent acceptance of garbage plaintext. Both mechanisms were confirmed via direct reading (not just via the
  passing `AesKeyRingResolver`/`EncryptedKeyRingFileStore`/`CloudAccountRepository`/`ProviderAccessTokenProtector`
  test suites, though those were also spot-checked for meaningful assertions). No issues found.
- **P-04 — CI/CD deployment gate: R-013's original "no in-workflow deployment gate; production deploys on every
  push to master" concern is meaningfully improved, unprompted by any audit recommendation.** Since the second
  audit, `.github/workflows/build-api.yml` was replaced with two separate workflows:
  `.github/workflows/deploy-stage.yml` (triggers on every push to `master`, builds and deploys **only** to the
  `stage` environment — `stage.google.biatec.io`/`stage.oidc.biatec.io`, a separate set of Kubernetes resources per
  `docs/STAGE_ENVIRONMENT.md`) and `.github/workflows/promote-production.yml` (`workflow_dispatch`-only — requires
  a human to manually trigger it, supplying a specific, already-built image tag to promote; it never rebuilds
  anything, so what reaches production is by construction something that was already deployed to and presumably
  exercised in stage first). This does not eliminate R-013's underlying GitHub-branch-protection-settings
  unverifiability (still unconfirmable from repository content alone, so R-013's likelihood is not revised to
  zero), but it does directly address the specific "every push to master deploys straight to production with no
  gate" mechanism the entry's Description names — a compromised or accidental push to `master` today reaches only
  a non-production environment automatically, with a manual, deliberate step required before it can reach
  production. Recommend R-013's likelihood be revised downward to reflect this; see §6.

## 5. Remediation tracking

| Prior ID | Title | This audit's finding |
|---|---|---|
| R-001 through R-012, R-014, R-016 | (see prior reports) | Not re-derived from scratch this pass; spot-checked via the restructured code's equivalent locations (e.g. `RedirectUriMatcher`, `AesEncryptionHelper`, `FixedTimeSecretsEqual`-equivalent patterns) during the course of reviewing the new features that depend on them, with no regression observed. A full line-by-line re-verification of every one of these, as the second audit performed against the first, was out of scope for this pass given this audit's priority was the substantial genuinely-new surface area (see Methodology) — recommend a future audit still periodically re-perform that full sweep, per `AUDITS-INSTRUCTIONS.md`'s annual-cadence requirement, rather than relying indefinitely on this audit's narrower spot-checks. |
| R-013 | CI/CD pipeline has no in-workflow deployment gate | **Materially improved** (unprompted). See P-04. Production deploys now require a manual `workflow_dispatch` promotion of an already-stage-tested image; likelihood revised downward — see §6. GitHub branch-protection settings remain unverifiable, so not closed outright. |
| R-018 | MCP tool responses leak raw exception messages | **Still open, unchanged.** Re-confirmed present at the same three tools' catch blocks. |
| R-019 | Committed `AesOptions` key/IV of unconfirmed live status | **Still substantively open.** Byte-identical key/IV confirmed unchanged since `d3b9a19`; `KeyId`/`ActiveKeyId` cosmetically relabeled to `"placeholder"` (partial progress toward the prior audit's recommendation #2); no fail-fast guardrail added (recommendation #3 still outstanding). Scope extended to cover the new `ProviderTokenProtection` key ring — see P-01. |

## 6. Risk registry changes

- **New entry H-01 → R-020 (High):** Vault-backup OAuth CSRF / cross-account write. Opened at a likelihood
  reflecting that exploitation requires social engineering (a victim must click a link and complete a consent
  screen) but no privileged access and no prior relationship with the victim — a realistic bar for a targeted or
  even semi-automated phishing-style campaign, especially since this system's user base (people who self-custody
  cryptocurrency) is a demonstrated high-value phishing target industry-wide.
- **New entry M-01 → R-021 (Medium):** No optimistic-concurrency control on seed-vault writes. Opened at low-to-
  moderate likelihood (requires a race condition, not an active attacker) but real impact (silent loss of a
  seed-creation or primary-switch).
- **New entry M-02 → R-022 (Medium):** Known moderate-severity advisory in pinned JWT-handling packages. Opened
  reflecting genuine uncertainty about exploitability against this codebase's specific usage (this audit did not
  fetch the advisory's full technical detail) balanced against the package's central role in this system's trust
  model.
- **R-013:** Likelihood revised from 10% down to 5% — reasoning: the specific mechanism the entry's Description
  names ("build-api.yml deploys directly to production on every push to master with no environment: protection
  block") no longer describes the current pipeline; production now requires a manual `workflow_dispatch` promotion
  step of an already-stage-deployed image (P-04). The entry is not closed because GitHub branch-protection settings
  for `master` remain unverifiable from repository content alone (this audit still has no access to check them),
  and a compromised or careless manual promotion is still possible in principle — but the specific "any push
  reaches production automatically" risk this entry was opened to describe is gone.
- **R-018, R-019:** Status/likelihood left unchanged — both re-confirmed still open with no material change in the
  underlying facts since the second audit (see §4/§5 for the specific re-verification evidence).
- **R-019's scope note:** This audit did not open a separate registry entry for the `ProviderTokenProtection` key
  ring (P-01) but recommends the next audit (or engineering, if remediated first) formalize this either as an
  explicit sub-bullet of R-019 or a linked sibling entry, since the two key rings share the identical unresolved
  question (is the committed value ever live?) and the identical recommended remediation.
- No risks were closed by this audit. R-001–R-012/R-014/R-016 were spot-checked, not exhaustively re-derived (see
  §5's caveat) — this audit does not claim to have independently re-verified them to the same depth the second
  audit did for the first, and their registry entries are left as the second audit last confirmed them.
- R-017 (accepted/unmitigable, total loss of funds) is carried forward unchanged — nothing in this engagement's
  scope bears on that risk.

## 7. Signature

**Claude Code (Claude Sonnet 5, Anthropic)**, auditor signature `claude-code-ai-review-3` — AI-assisted static
code review plus local build/test execution, performed 2026-08-01 against commit `69d410c` (full range reviewed:
`173793a..HEAD`), at the request of and with full repository access granted by Scholtz & Company, j.s.a. No
cryptographic signature is attached to this report file; the report's integrity should instead be verified against
this repository's own git history (the commit that adds this file, and its hash, are the authoritative record of
this report's original content). As disclosed in §1, this report does not constitute an independent third-party
audit and should be supplemented by one before being relied upon as external assurance for accounts holding
material value — this recommendation is unchanged from both prior audits and is not weakened by this audit's
findings. Given this audit identified a High-severity finding (H-01) in a feature (cross-cloud vault backup) that
appears production-facing per its own controller/service naming, the repository owner should treat H-01 as the
single most time-sensitive item in this report and confirm whether that feature is currently reachable by real
users before this report's publication, per `AUDITS-INSTRUCTIONS.md`'s "Publication" section's coordinated-
disclosure guidance for unresolved High findings.
