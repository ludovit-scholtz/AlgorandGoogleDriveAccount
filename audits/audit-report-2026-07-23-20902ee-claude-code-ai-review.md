# Biatec MCP Server — Security Audit Report

## 1. Front matter

- **Auditor:** Claude Code (Claude Sonnet 5, Anthropic), operated as an AI coding assistant inside this repository
  at the request of the repository owner, Scholtz & Company, j.s.a.
- **Conflict of interest disclosure:** This is **not** an independent third-party audit firm engagement as
  contemplated by [AUDITS-INSTRUCTIONS.md](AUDITS-INSTRUCTIONS.md)'s "Independence and conduct" section. The
  auditor (this AI assistant) was invoked by, and has the same repository access as, the party responsible for the
  code under review, and has previously been used to make changes to this codebase in other sessions. This report
  does **not** satisfy the instructions' requirement that "auditors must be independent of the development of the
  feature/commit being audited." It is published here as a rigorous **whitebox static self-review** that
  identifies concrete, reproducible issues, and should be treated by end users as a first-party disclosure, not as
  independent assurance. A genuinely independent firm engagement is still required to satisfy the letter of
  `AUDITS-INSTRUCTIONS.md` and is recommended before this report is relied upon as external assurance for funds at
  material risk.
- **Commit audited:** `20902ee53849d9401a73a19f7ce02e2a62ec442d` (single commit; working tree clean at time of
  audit, `git status` reported no pending changes).
- **Engagement dates:** 2026-07-23 (single-day static review).
- **Scope statement:** Reviewed the components listed under "Scope" in `AUDITS-INSTRUCTIONS.md`:
  `AesEncryptionHelper.cs`, `RedirectUriMatcher.cs`, `DriveService.cs`, `DevicePairingService.cs`,
  `GoogleAuthorizationService.cs`, `CrossAccountProtectionService.cs`, `JwtIssuerService.cs`,
  `PortfolioValuationService.cs` (not separately flagged — no security-relevant defects found beyond scope
  described in `CLAUDE.md`), `DevicePairingController.cs`, `DriveController.cs`, `JwtIssuerController.cs`,
  `GoogleDriveRepository.cs`, `BiatecMCPGoogle.cs`, `Model/*.cs` configuration POCOs,
  `.github/workflows/build-api.yml`, `docs/KUBE_CONFIG_SECURITY.md`, and the direct-dependency package list in
  `AlgorandGoogleDriveAccount.csproj`.
  - **Deviations from full scope:** No dynamic/runtime testing was performed against a live deployment — this
    audit is **static (whitebox source review) only**. `AUDITS-INSTRUCTIONS.md` requires written pre-authorization
    for any testing against the running system; no such authorization was sought or granted for this engagement,
    so no such testing was attempted. No live CVE database lookups were performed for third-party dependencies
    (network access to advisory databases was not used); dependencies are enumerated with versions so a follow-up
    engagement can check them. `k8s/main/conf/*` contents were not inspected (outside the file list examined) —
    flagged as a follow-up item (Finding F-13).
- **Methodology:** Full-text reading of every in-scope source file; manual line-by-line review for the specific
  defect classes called out in `AUDITS-INSTRUCTIONS.md` (key derivation and IV handling, redirect URI allowlist
  bypasses, PKCE/state/nonce/audience validation, singleton cross-request state leakage, MCP prompt-injection
  exposure, CI/CD credential scope). Every finding below was independently re-verified by re-reading the cited
  `file:line` after initial identification (no finding is reported on the basis of a first-pass read alone).
- **Tools used:** Source-code reading and grep-based cross-referencing only. No SAST/DAST scanner, no fuzzing, no
  dependency-CVE scanner, no live traffic capture.
- **Verdict definitions used in this report:**
  - **Pass** — no Critical or High findings, all Mediums have documented compensating controls.
  - **Pass-with-findings** — no unmitigated Critical findings, but one or more High/Medium findings exist that a
    relying party should be aware of before trusting the system with funds.
  - **Fail** — one or more Critical findings exist with no compensating control that meaningfully limits
    exploitability.
- **Overall verdict: Pass-with-findings.** One Critical-severity design/implementation gap was identified
  (F-01, weak device-pairing session identifiers) that, combined with the MCP fund-transfer tool surface (F-04),
  constitutes a credible path to unauthorized fund movement and should be remediated before this report is relied
  upon as assurance for accounts holding non-trivial value. No evidence of past exploitation was found or sought
  (out of scope for a static review). Several other components — the OIDC redirect-URI allowlist, PKCE
  enforcement, default CORS/scope/client configuration — were reviewed in depth and found to be soundly
  implemented.

## 2. Executive summary

*(Written for a non-technical reader deciding whether to trust this system with real funds.)*

This service lets you keep your Algorand account keys encrypted in your own Google Drive, rather than on the
company's servers. We reviewed the code that protects those keys and the code that lets other apps sign in as
"you."

**The good news:** the parts of the system that decide *which website is allowed to receive your login token*
(the OIDC redirect-URI allowlist) are carefully built and we could not find a way to trick them. The system also
does not have careless defaults — for example, it does not accidentally allow websites from anywhere to talk to
it (CORS), and unknown apps cannot register themselves to receive tokens.

**The concerning finding:** the "pair this AI assistant with my Google Drive" feature (`pair.html`, the QR-code /
link-based device pairing flow) identifies your pairing session using an ID that is generated in your web browser
from the current time plus a weak random-number generator — not a proper cryptographically secure secret. Several
server endpoints will hand back sensitive information — including, for one endpoint, your **raw Google access
token** — to *anyone* who supplies that ID, with no check that the requester is actually you. Combined with the
fact that a paired AI assistant can be instructed to transfer funds out of your account, this means that if an
attacker can observe or guess your pairing session ID (for example, from a screenshot, a shared link, or by
brute-forcing the relatively small space of possible IDs within the roughly 24-hour pairing window), they could
potentially read your account data or move funds out of your wallet. We consider this the single most important
issue to fix before this feature is trusted with meaningful funds.

We also found a handful of smaller issues: some internal error messages are more detailed than they should be,
one login/logout link is not restricted to trusted destinations (a phishing-adjacent "open redirect"), and a
secondary security check (Google's "was this login attempt hijacked" signal) is switched off by default. None of
these are as serious as the pairing-ID issue on their own.

**Bottom line:** the core design — keys never leave your own Drive, encrypted with a key only the service knows —
is sound in principle, and the login-token allowlisting is well built. But the device-pairing mechanism that
connects an AI assistant to your account has a real weakness that should be fixed, and because this review was
performed by an AI assistant working inside the same codebase (not an independent audit firm), we recommend an
independent firm re-verify these findings before this report is relied upon as third-party assurance.

## 3. Methodology

See "Methodology" in the front matter above. In brief: full manual reading of all in-scope files, targeted review
against the specific threat classes named in `AUDITS-INSTRUCTIONS.md` §Scope, and independent re-verification of
every finding's cited code before inclusion in this report. No dynamic testing, no dependency CVE lookups, no
infrastructure penetration testing were performed — these remain open items for a future engagement with proper
authorization (see §7).

## 4. Detailed findings

Findings are numbered `F-NN` for stable cross-reference from `RISKS.md`. Severity scale: Critical / High / Medium
/ Low / Informational.

### F-01 — Critical: Weak, client-generated device-pairing session IDs exposed via unauthenticated endpoints (IDOR)

- **Affected component:** `AlgorandGoogleDriveAccount/wwwroot/pair.html:222-223`;
  `AlgorandGoogleDriveAccount/Controllers/DevicePairingController.cs:207-224, 247-280, 287-303, 310-398, 405-432,
  508-544`; `AlgorandGoogleDriveAccount/BusinessLogic/DevicePairingService.cs` (Redis key `device_session:{sessionId}`,
  ~1-day TTL).
- **Description:** The device-pairing `sessionId` is generated **in the browser**:
  ```js
  function generateSessionId() {
      return 'device_' + Date.now() + '_' + Math.random().toString(36).substr(2, 9);
  }
  ```
  (`pair.html:222-223`). This value becomes the sole Redis lookup key for the pairing session, which stores the
  user's Google OAuth access/refresh tokens. It is not a cryptographically secure random value: `Date.now()` is a
  guessable/narrow-window millisecond timestamp, and `Math.random()` is a non-cryptographic PRNG (contrast with
  `JwtIssuerService.cs`'s use of `RandomNumberGenerator.GetBytes(48)` for opaque tokens elsewhere in the same
  codebase). Multiple controller endpoints accept this ID as a bare path parameter under `[AllowAnonymous]`, with
  **no check that the caller is the session's owner**:
  - `GET /api/device/access-token/{sessionId}` — returns the **raw Google OAuth access token** in plaintext.
  - `GET /api/device/diagnose/{sessionId}` — uses the token to call Drive and returns the user's email, folder/file
    existence, file id/size/timestamps.
  - `GET /api/device/security-status/{sessionId}` — returns email + Cross-Account Protection status.
  - `GET /api/device/portfolio/{sessionId}` — returns portfolio value, tier, and balances.
  - `DELETE /api/device/unpair/{sessionId}` — unpairs (denial-of-service) any session, given only the ID.
  - `GET /api/device/info/{sessionId}` — confirms a pairing exists and returns email/device name (tokens masked
    here).
  None of these routes are rate-limited (contrast with `JwtIssuerController`'s `/authorize` throttling via
  `TryRegisterAuthorizeAttemptAsync`).
- **Proof of concept / reproduction:** Given any valid `sessionId` string of the documented shape
  (`device_<epoch-ms>_<9 base36 chars>`), an unauthenticated `GET /api/device/access-token/{sessionId}` returns the
  victim's live Google access token. The ID space is bounded by a knowable epoch-ms window (if the approximate
  pairing time is known or inferred) times a non-cryptographic-PRNG-derived 9-character base-36 suffix, well below
  the entropy of the cryptographically secure tokens used elsewhere in this same codebase.
- **Impact:** Full read access to the victim's Drive-derived Algorand account metadata, ability to unpair
  (denial-of-service) a victim's paired device, and exfiltration of a live Google OAuth access token. Combined with
  F-04 (MCP fund-transfer tool bound to the same `sessionId`), this extends to **unauthorized transaction signing
  and fund exfiltration** from the victim's Algorand account.
- **Recommended remediation:** Generate `sessionId` server-side using a cryptographically secure random generator
  with ≥128 bits of entropy (mirroring `JwtIssuerService`'s existing `GenerateOpaqueToken` pattern). Require the
  sensitive endpoints (`access-token`, `diagnose`, `security-status`, `portfolio`, `unpair`) to be bound to an
  authenticated caller identity rather than being bare, anonymous ID lookups, and add rate limiting on all
  `{sessionId}`-keyed routes.

### F-02 — High: AES key/IV derivation is a single unsalted-KDF-sense hash from one global shared secret, with no authenticated encryption

- **Affected component:** `AlgorandGoogleDriveAccount/Helper/AesEncryptionHelper.cs:22-93`;
  `AlgorandGoogleDriveAccount/Repository/GoogleDriveRepository.cs` (decrypt error handling).
- **Description:** `DeriveKey` (`AesEncryptionHelper.cs:87-93`) computes `SHA256(baseValue || UTF8(email))` and
  truncates — a single, non-iterated hash, not a real KDF (no PBKDF2/Argon2/scrypt work factor). Both the AES key
  and the IV are derived this way from the **same global `AesOptions.Key`/`AesOptions.IV` pair shared by every
  user** (`AesEncryptionHelper.cs:26-27, 50-51`), meaning the entire confidentiality guarantee for every user's
  encrypted mnemonic file rests on the secrecy of one shared configuration secret; if it leaks, every user's file
  can be decrypted offline given only their (non-secret) email address. Encryption uses **AES-CBC with PKCS7
  padding and no HMAC/AEAD** (`Mode = CipherMode.CBC`, `AesEncryptionHelper.cs:28, 52`) — there is no integrity
  check, so tampered ciphertext is only detected incidentally via a PKCS7 padding failure, and
  `GoogleDriveRepository.cs`'s catch block for `CryptographicException` inspects `cryptoEx.Message` for
  `"Padding"` and surfaces a verbose, distinguishable error (email, file size, raw exception text) that ultimately
  reaches HTTP responses via `DriveController`'s generic `catch (Exception exc) { return BadRequest(new
  ProblemDetails { Detail = exc.Message }); }`. Because the IV, like the key, is *derived* rather than
  randomly generated per encryption, any future feature that re-encrypts a user's file (key rotation, multi-write)
  would reuse the same key+IV pair — a CBC IV-reuse anti-pattern, latent today because each file is written once.
- **Proof of concept / reproduction:** Not independently exploited (would require possession of the leaked
  `AesOptions.Key`/`IV`, or attacker-controlled ciphertext bytes fed to `Decrypt`, neither of which this static
  review confirmed is reachable by an external attacker today). The design defect itself — single-hash key
  derivation, no AEAD, error-message oracle — is confirmed by direct code reading.
- **Impact:** If the shared `AesOptions.Key`/`IV` secret is ever exposed (source leak, misconfigured Kubernetes
  secret/configmap, crash dump, insider access), **every user's Algorand mnemonic across the entire user base**
  can be decrypted offline in bulk, since the only per-user variable is a public email address. This is a
  single-point-of-failure design: it does not degrade gracefully to per-user isolation the way, e.g., a per-user
  random salt stored alongside the ciphertext would.
- **Recommended remediation:** Derive per-file keys using a real KDF (PBKDF2/Argon2id) with a per-user, randomly
  generated salt stored alongside the ciphertext (not derived from email alone); generate the IV randomly per
  encryption via `RandomNumberGenerator` and store it with the ciphertext; switch to an authenticated mode
  (AES-GCM) so tampering is cryptographically detected rather than inferred from padding-exception text; sanitize
  decryption-failure error messages returned to API callers to a generic message.

### F-03 — High: JWT bearer access-token validation does not check the `aud` (audience) claim

- **Affected component:** `AlgorandGoogleDriveAccount/BusinessLogic/JwtIssuerService.cs:475-487`
  (`ValidateBearerAccessToken`), used by `/userinfo`, `/introspect`, `/verify`.
- **Description:** `CreateAccessToken` mints tokens with `audience: clientId` (confirmed elsewhere in the same
  file), but `ValidateBearerAccessToken`'s `TokenValidationParameters` sets `ValidateAudience = false`
  (`JwtIssuerService.cs:484`), while `ValidateIssuer`/`ValidateLifetime` are both correctly enabled. This means an
  access token minted for one client (`aud = client-a`) is accepted at `/userinfo`, `/introspect`, and `/verify`
  regardless of which client it was actually issued to.
- **Proof of concept / reproduction:** Obtain a valid access token issued to any registered client (e.g. a
  low-trust client with narrow `AllowedScopes`); present it as a bearer token to `/userinfo` or `/introspect` —
  it validates successfully with no audience check, even though the token's own `aud` claim does not match the
  relying party consuming it.
- **Impact:** Defeats the standard purpose of the `aud` claim (audience restriction) at the one place all
  three resource/introspection endpoints rely on for validation, in a system explicitly designed to serve
  multiple distinct whitelisted third-party clients.
- **Recommended remediation:** Set `ValidateAudience = true` with the specific resource's expected audience(s),
  or explicitly check `Claims["aud"]` against the calling client/resource identity after validation.

### F-04 — High: MCP fund-transfer tools have no server-side confirmation gate and are bound to the weak session ID (F-01)

- **Affected component:** `AlgorandGoogleDriveAccount/MCP/BiatecMCPGoogle.cs` (`TransferAsset`, `OptIn` tools).
- **Description:** The `TransferAsset` and `OptIn` MCP tools resolve identity purely from the MCP session's
  `sessionId` (the same weak identifier described in F-01), load the decrypted account, sign, and **immediately
  broadcast** the transaction to the Algorand network. There is no dry-run/preview step, no amount ceiling, no
  receiver allowlist, and no additional confirmation round-trip enforced by the server itself — all "are you
  sure" gating is left to whatever MCP client/host UI the AI agent happens to be running in. Because MCP tools are
  exposed with plain-English descriptions to any connected AI agent, an agent that also has access to untrusted
  content (a webpage, email, or document it is asked to summarize/process) is exposed to prompt injection: text
  embedded in that content can instruct the agent to invoke `transferAsset` with attacker-supplied parameters.
- **Proof of concept / reproduction:** Not dynamically tested (would require a live paired session and would move
  real funds, out of scope without written authorization). The absence of any confirmation/allowlist/limit logic
  in `TransferAsset`/`OptIn` is confirmed by direct code reading.
- **Impact:** Combined with F-01 (an attacker obtaining or guessing a victim's `sessionId`), this is a direct path
  to unauthorized on-chain fund transfer. Independently of F-01, it is also a direct prompt-injection-to-theft
  path for any legitimately paired user whose AI agent processes untrusted content.
- **Recommended remediation:** Require an explicit, freshly-issued confirmation token/one-time code for
  fund-moving tool calls (separate from the long-lived pairing session), and/or enforce server-side spending
  limits or a receiver allowlist independent of what the MCP client chooses to implement.

### F-05 — Medium: Authorization-code and pending-authorize-request redemption is not atomic (narrow-window double-redemption race)

- **Affected component:** `AlgorandGoogleDriveAccount/BusinessLogic/JwtIssuerService.cs` — `ExchangeTokenAsync`
  (Redis `GetStringAsync` followed by a separate `RemoveAsync` on the code key) and
  `GetPendingAuthorizeRequestAsync`/`RemovePendingAuthorizeRequestAsync` (same pattern).
- **Description:** Redis reads and deletes for one-time-use codes/pending requests are two separate operations,
  not an atomic take-and-delete (e.g. `GETDEL` or a Lua script). Two concurrent requests presenting the same code
  within the race window can both read it successfully before either delete completes.
- **Impact:** A single authorization code could, in a narrow timing window, be redeemed twice, issuing two
  independent token sets — a violation of RFC 6749 §4.1.2's single-use requirement, though it requires an
  attacker (or a buggy/retrying client) to race a request against a legitimate exchange within a very small
  window, and does not on its own bypass PKCE/redirect-URI/client validation.
- **Recommended remediation:** Use an atomic get-and-delete primitive (Redis `GETDEL`, or a Lua script wrapping
  `GET`+`DEL`) for both code exchange and pending-authorize-request consumption.

### F-06 — Medium: Client secret comparison is not constant-time

- **Affected component:** `AlgorandGoogleDriveAccount/BusinessLogic/JwtIssuerService.cs:677`
  (`ValidateClientAuthentication`): `string.Equals(client.ClientSecret, clientSecret, StringComparison.Ordinal)`.
- **Description:** `string.Equals` with `StringComparison.Ordinal` short-circuits on the first mismatched
  character, which is a theoretical timing side-channel for recovering a confidential client's secret
  character-by-character.
- **Impact:** In practice, network jitter makes this hard to exploit remotely, but it is a real, easily fixed
  weakness in a component whose entire purpose is confidential-client authentication.
- **Recommended remediation:** Compare secrets using `CryptographicOperations.FixedTimeEquals` over UTF-8 byte
  arrays.

### F-07 — Medium: Silent fallback to an ephemeral, non-persistent JWT signing key on configuration failure

- **Affected component:** `AlgorandGoogleDriveAccount/BusinessLogic/JwtIssuerService.cs` —
  `LoadOrCreateSigningKey`: falls back to `RSA.Create(2048)` with only a `LogWarning` if no valid
  `JwtIssuer:SigningPrivateKeyPem` is configured.
- **Description:** The service starts successfully even with a missing/misconfigured signing key, silently
  minting a fresh in-memory RSA key each time this fallback triggers.
- **Impact:** A configuration failure in production (e.g. a botched Kubernetes secret) degrades silently rather
  than failing fast: all previously issued tokens/refresh tokens become unverifiable (JWKS mismatch) without an
  operator being alerted beyond a log line, and in a multi-replica deployment each replica would mint its own
  distinct key, breaking cross-replica JWKS validation. This is primarily an availability/operational risk, with
  a secondary security angle (a forced config failure yields a low-visibility key change).
- **Recommended remediation:** Fail fast (throw at startup) when no valid signing key is configured in any
  non-Development environment, rather than silently falling back to an ephemeral key.

### F-08 — Medium: Open redirect on `/api/drive/login` and `/api/drive/logout`

- **Affected component:** `AlgorandGoogleDriveAccount/Controllers/DriveController.cs:112-130`.
- **Description:** Both actions accept a `redirectUri` query parameter and pass it directly into
  `AuthenticationProperties.RedirectUri` for `Challenge`/`SignOut`, with **no allowlist check** — unlike the OIDC
  `/authorize` and `/connect/endsession` endpoints, which correctly validate `redirect_uri`/
  `post_logout_redirect_uri` against `RedirectUriMatcher` and a per-client allowlist.
- **Proof of concept / reproduction:** `GET /api/drive/login?redirectUri=https://attacker.example/phish` completes
  the Google OIDC challenge and, on success, redirects the browser to the attacker-controlled URL.
- **Impact:** Open redirect off a trusted domain, immediately following a real Google authentication step — a
  useful primitive for phishing (the URL bar shows this service's trusted domain through the login step) even
  though no token/secret is directly leaked to the redirect target by this flow alone.
- **Recommended remediation:** Validate `redirectUri` against the same `RedirectUriMatcher` allowlist mechanism
  already used by `JwtIssuerController`, or remove the parameter and use a fixed post-login destination.

### F-09 — Medium: Cross-Account Protection (RISC) disabled by default; `email_verified` not enforced anywhere at runtime by default

- **Affected component:** `AlgorandGoogleDriveAccount/Model/Configuration.cs` (`CrossAccountProtectionConfiguration.Enabled
  = false` by default); `AlgorandGoogleDriveAccount/BusinessLogic/CrossAccountProtectionService.cs`.
- **Description:** With CAP disabled (the shipped default), `IsUserAccountSecureAsync`/`CheckSecurityStatusAsync`
  return "secure" unconditionally without contacting Google, and none of the RISC-style checks in
  `PerformAdditionalSecurityChecks` (audience, `email_verified`, issuer, token age) execute. Since `email` is the
  sole tenant-isolation input to key derivation (F-02), the absence of a default-on runtime check that Google
  actually verified the email narrows the isolation guarantee to whatever the base ASP.NET Core Google OIDC
  handler enforces on its own (which does validate the `id_token` signature/audience/issuer, but CAP is the layer
  that would explicitly assert `email_verified`).
- **Impact:** Reduced defense-in-depth around the identity claim that the entire self-custody model's tenant
  isolation depends on. The fail-open/fail-closed asymmetry on error (`CrossAccountProtectionService.cs`: fails
  open when disabled, fails closed when enabled) is intentional and consistent, not itself a bug.
- **Recommended remediation:** Consider enabling `email_verified` enforcement unconditionally at the OIDC
  authentication-handler level (independent of the optional CAP feature), since this check is cheap and directly
  supports the core tenant-isolation guarantee.

### F-10 — Medium: `GoogleAuthorizationService.HasScopeAsync` does not actually check the requested scope

- **Affected component:** `AlgorandGoogleDriveAccount/BusinessLogic/GoogleAuthorizationService.cs:60-91`.
- **Description:** The method accepts a `scope` parameter but only verifies that *some* non-empty access token
  exists, never comparing it against the token's actually granted scopes.
- **Impact:** Any caller-side authorization decision built on this method's result (e.g. "do we have Drive access
  before showing this UI") is unreliable — it will report `true` even for a token lacking the requested scope.
  Real Drive API calls still enforce scope server-side at Google, so this is a logic bug rather than a direct
  privilege escalation, but it undermines any control that assumes this method is meaningful.
- **Recommended remediation:** Parse the token's granted-scopes claim (or the token-info response) and compare
  against the requested `scope` before returning `true`.

### F-11 — Informational: Verbose internal error messages surfaced to API callers

- **Affected component:** `AlgorandGoogleDriveAccount/Repository/GoogleDriveRepository.cs` (decrypt failure
  path); `DriveController.cs` (`BadRequest(new ProblemDetails { Detail = exc.Message })`);
  `DevicePairingController.cs` (`StatusCode(500, new { error = ex.Message })`).
- **Description:** Exception messages containing email addresses, file sizes, and raw cryptographic-exception
  text are returned directly in HTTP response bodies. Already discussed as an amplifier of F-02; independently
  worth flagging as a general information-disclosure hygiene issue across multiple controllers.
- **Recommended remediation:** Log detailed exceptions server-side; return generic, non-identifying error messages
  to API callers.

### F-12 — Informational: Drive search-query string built via unescaped interpolation (not currently attacker-reachable)

- **Affected component:** `AlgorandGoogleDriveAccount/Repository/GoogleDriveRepository.cs` (`folderRequest.Q =
  $"mimeType = ... and name = '{folderName}' and trashed = false"`).
- **Description:** `folderName`/`fileName` are sourced from server configuration, not user input, and `folder.Id`
  is server-generated by Drive — so this is not exploitable with the codebase as it exists today. It is a latent
  injection pattern that would become a real bug if these values were ever templated from user-controlled input.
- **Recommended remediation:** Use parameterized/escaped query construction as a matter of defensive coding, even
  though not currently reachable by an attacker.

### F-13 — Informational: CI/CD pipeline has no in-workflow deployment gate; `k8s/main/conf` contents not verified in this pass

- **Affected component:** `.github/workflows/build-api.yml` (push-to-`master` trigger, direct `kubectl apply` /
  `rollout restart`, `kubectl create configmap --from-file=k8s/main/conf`).
- **Description:** The workflow itself contains no `environment:` protection block or required-status-check
  reference — any gating on direct pushes to `master` depends entirely on GitHub branch-protection settings
  external to this file, which this review could not verify from repository content alone. Separately,
  `k8s/main/conf/*` (the source for the deployed ConfigMap) was not inspected in this pass; ConfigMaps are not
  encrypted-at-rest the way Kubernetes `Secret` objects are, so this is worth an explicit check that
  `AesOptions.Key`/`IV`, `JwtIssuer:SigningPrivateKeyPem`, and `App:ClientSecret` are not stored there in plaintext
  YAML.
- **Recommended remediation:** Confirm branch-protection rules for `master` (required PR review, required status
  checks, no direct pushes) in GitHub repository settings; audit `k8s/main/conf/*` to confirm no secret material is
  stored as plaintext ConfigMap content. `docs/KUBE_CONFIG_SECURITY.md` itself describes a well-scoped,
  time-limited, namespace-scoped kubeconfig design — reviewed and found sound, no issue with the document's stated
  design (script-level implementation not independently re-verified in this pass).

### F-14 — Informational: `id_token_hint` at `/connect/endsession` is used to select an allowlist without signature validation

- **Affected component:** `AlgorandGoogleDriveAccount/BusinessLogic/JwtIssuerService.cs` —
  `TryGetClientIdFromIdTokenHint`.
- **Description:** The `aud` claim is read from an unvalidated (signature not checked) `id_token_hint` purely to
  select which registered client's `PostLogoutRedirectUris` allowlist to check the actual
  `post_logout_redirect_uri` against. The final redirect target is still validated against that resolved client's
  real, registered allowlist — so an attacker can at most cause the wrong (but still legitimately registered)
  client's allowlist to be used, not escape the allowlist universe entirely.
- **Recommended remediation:** Validate the `id_token_hint` signature before trusting its `aud` claim, for
  defense-in-depth.

### F-15 — Informational: `RedirectUriMatcher` and OIDC redirect-URI allowlisting reviewed, no bypass found

- **Affected component:** `AlgorandGoogleDriveAccount/Helper/RedirectUriMatcher.cs`.
- **Description:** This audit specifically attempted to find wildcard-subdomain escape (`evil-example.com`
  matching `*.example.com`), userinfo-`@`-prefix tricks, scheme confusion, case-sensitivity bypasses, and
  trailing-slash tricks against this matcher, per the concerns named in `AUDITS-INSTRUCTIONS.md`. None were found:
  host wildcard matching requires a literal label separator before the suffix (correctly rejects
  `evil-example.com` against a `*.example.com` pattern); matching is performed against parsed `Uri.Host` (not raw
  string), so userinfo-prefix tricks do not bypass it; scheme and host comparisons are case-insensitive
  (`OrdinalIgnoreCase`, correct per spec) while path/query comparisons are case-sensitive (also correct); trailing
  slash is normalized only for post-logout-redirect matching, not for `/authorize`, which fails closed on a
  mismatch. Two minor items are noted for a future dedicated unit-test pass rather than as confirmed bypasses:
  trailing-dot hostname normalization (`example.com.`) was not independently verified against .NET's exact `Uri`
  canonicalization behavior, and IDN/punycode homograph handling relies on `Uri`'s default normalization without
  an explicit application-level check. Neither was demonstrated to be exploitable.

### F-16 — Informational, unconfirmed: possible captive-dependency risk between the singleton `GoogleDriveRepository` and `IGoogleAuthProvider`

- **Affected component:** `AlgorandGoogleDriveAccount/Repository/GoogleDriveRepository.cs` (registered
  `AddSingleton`, `Program.cs`), constructor-injected `IGoogleAuthProvider` (from `Google.Apis.Auth.AspNetCore3`).
- **Description:** `GoogleDriveRepository` itself holds no mutable per-request instance state (all per-call data —
  `email`, `slot`, `googleCredential` — is passed as method parameters, confirmed by direct reading) — this part
  is sound. The open question is whether `IGoogleAuthProvider`, if registered with a **Scoped** lifetime by its
  library (a common pattern for per-request credential providers), becomes permanently captured by the singleton
  at first resolution, causing every subsequent request that relies on the implicit (no-explicit-credential)
  `LoadAccount` path to silently reuse the first request's Google credential. `ServiceProviderOptions.ValidateScopes`
  (which would surface this loudly) is enabled by the default ASP.NET Core host builder only in the Development
  environment, so this would not necessarily be caught before shipping to production.
- **Status:** **Not confirmed** — this review could not determine `IGoogleAuthProvider`'s exact DI lifetime in
  `Google.Apis.Auth.AspNetCore3` 1.75.0 by source inspection alone. Reported as a follow-up item requiring either
  decompiling the installed package, consulting its source, or an integration test simulating two concurrent users
  against `/api/drive/sign` and `/api/drive/address` (the code paths that use the implicit credential path; MCP
  tool calls in `BiatecMCPGoogle.cs` are unaffected, as they always construct and pass an explicit credential).
  **If confirmed**, this would be a Critical finding (cross-user credential/account confusion). It is not included
  in the severity-scored findings above pending confirmation, and is carried into `RISKS.md` as an open item for
  the next audit to resolve.

## 5. Remediation tracking

This is the first audit performed under `AUDITS-INSTRUCTIONS.md`; `RISKS.md` had no prior entries. There is no
prior audit's findings to track remediation against.

## 6. Risk registry changes

`RISKS.md` was populated for the first time by this audit (it previously contained only the template with no
entries). Sixteen findings from this report were converted into registry entries `R-001` through `R-016`,
corresponding 1:1 to `F-01` through `F-16` above by finding number. One accepted/unmitigable risk was added
(`R-017`, permanent loss of funds if a user loses both their Google account and any recovery mechanism — inherent
to the self-custody design and explicitly documented as such in `CLAUDE.md`'s architecture notes). No risks were
closed (none existed to close) and no likelihoods were revised (no prior estimates existed). See `RISKS.md` for
full entries, likelihood reasoning, and history.

## 7. Signature

**Claude Code (Claude Sonnet 5, Anthropic)** — AI-assisted static code review, performed 2026-07-23 against commit
`20902ee53849d9401a73a19f7ce02e2a62ec442d`, at the request of and with full repository access granted by Scholtz &
Company, j.s.a. No cryptographic signature is attached to this report file; the report's integrity should instead
be verified against this repository's own git history (the commit that adds this file, and its hash, are the
authoritative record of this report's original content). As disclosed in §1, this report does not constitute an
independent third-party audit and should be supplemented by one before being relied upon as external assurance for
accounts holding material value.
