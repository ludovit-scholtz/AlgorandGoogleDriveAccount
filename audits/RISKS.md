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

## Open risks

### R-001 — Weak, client-generated device-pairing session IDs exposed via unauthenticated endpoints (IDOR)

- **Description:** `pair.html`'s `generateSessionId()` builds the device-pairing session ID from `Date.now()` +
  `Math.random()` (non-cryptographic PRNG) in the browser. This ID is the sole Redis lookup key protecting a
  user's Google OAuth tokens, and several `DevicePairingController` endpoints (`access-token`, `diagnose`,
  `security-status`, `portfolio`, `unpair`) are `[AllowAnonymous]` with no ownership check and no rate limiting,
  keyed only on this ID. A threat actor who observes (screenshot, shared link, referrer/analytics logs) or
  brute-forces a victim's session ID within its ~1-day TTL can read account data, exfiltrate a live Google access
  token, and (via R-004) potentially move funds.
- **Likelihood (5-year misuse probability):** 35% — reasoning: this requires either (a) social/opportunistic
  observation of a shared pairing link/screenshot, which is a plausible phishing/support-scam vector given this
  product's target audience of AI-assistant users copy-pasting URLs, or (b) active brute-forcing, which is
  tractable given the ID's limited entropy but requires sustained, detectable traffic against a public endpoint
  with no rate limiting today. No comparable public incident is known for this specific product, but weak
  session-token IDOR is one of the most common real-world web app vulnerability classes (consistently top-3 in
  OWASP-adjacent bug bounty data), and this system is a novel, low-traffic target today — likelihood should be
  revisited upward if user count/attacker attention grows before remediation.
- **Impact:** Account data disclosure, live OAuth token exfiltration, denial-of-service via forced unpair, and
  (combined with R-004) unauthorized fund transfer — full compromise of an individual paired account.
- **Affected component:** `AlgorandGoogleDriveAccount/wwwroot/pair.html:222-223`;
  `AlgorandGoogleDriveAccount/Controllers/DevicePairingController.cs`.
- **Current mitigations:** Session TTL bounded to ~1 day, limiting the brute-force window.
- **Recommended further mitigation:** Server-side cryptographically secure session ID generation (≥128 bits);
  ownership/authentication check on sensitive endpoints; rate limiting on all `{sessionId}` routes.
- **Status:** Open.
- **History:**
  - 2026-23-07 — claude-code-ai-review: opened at 35%, corresponds to finding F-01.

### R-002 — AES key/IV derivation is a single global-secret hash, not a per-user KDF; no authenticated encryption

- **Description:** All users' encrypted mnemonic files share one global `AesOptions.Key`/`IV` pair, with per-user
  differentiation coming only from a single unsalted SHA-256 hash of `key||email` (not a real KDF). Encryption is
  AES-CBC with no HMAC/AEAD, and decryption-failure error messages are verbose enough to act as a padding-error
  oracle if attacker-controlled ciphertext bytes are ever reachable.
- **Likelihood (5-year misuse probability):** 15% — reasoning: exploitation requires either the shared
  `AesOptions.Key`/`IV` secret leaking (a config/secret-management failure, not a code-level bug — mitigated by
  the namespace-scoped, time-limited Kubernetes credential design described in `docs/KUBE_CONFIG_SECURITY.md`
  reviewed under F-13) or an attacker gaining the ability to feed manipulated ciphertext into the decrypt path,
  which was not demonstrated to be reachable in this audit. Secret leakage from CI/CD or cloud misconfiguration is
  a realistic, recurring class of real-world incident industry-wide, which is why this is not rated near-zero
  despite no direct exploitation path being confirmed today.
- **Impact:** If realized, bulk offline decryption of every user's Algorand mnemonic — the most severe possible
  impact category for this system (total loss of the self-custody guarantee for the entire user base at once,
  not just one account).
- **Affected component:** `AlgorandGoogleDriveAccount/Helper/AesEncryptionHelper.cs`;
  `AlgorandGoogleDriveAccount/Repository/GoogleDriveRepository.cs` (error handling).
- **Current mitigations:** `docs/KUBE_CONFIG_SECURITY.md`-described scoped, time-limited CI credentials reduce
  (but do not eliminate) the chance of the shared AES secret leaking via the deployment pipeline.
- **Recommended further mitigation:** Per-user random salt + real KDF (PBKDF2/Argon2id); random IV per encryption
  stored with ciphertext; move to AES-GCM (AEAD); sanitize decrypt-failure error responses.
- **Status:** Open.
- **History:**
  - 2026-23-07 — claude-code-ai-review: opened at 15%, corresponds to finding F-02. Impact is rated far higher
    than likelihood because a realized event would affect the entire user base simultaneously, not one account.

### R-003 — JWT bearer access-token validation does not check the `aud` claim

- **Description:** `ValidateBearerAccessToken` sets `ValidateAudience = false`, so an access token issued to one
  OIDC client is accepted at `/userinfo`, `/introspect`, `/verify` regardless of its actual `aud` claim.
- **Likelihood (5-year misuse probability):** 10% — reasoning: exploitation requires the attacker to already
  possess a validly issued access token for *some* registered client (itself gated by the existing client
  allowlist and PKCE), and then present it against a different relying party's introspection/userinfo call — a
  cross-client confused-deputy scenario that requires multiple distinct, mutually distrusting relying parties to
  be integrated simultaneously, which is not yet the case for this young product's client base.
- **Impact:** Cross-client token replay / confused-deputy risk — a token minted for a low-trust client could be
  accepted where a high-trust client's token was expected, growing in relevance as more third-party clients are
  onboarded.
- **Affected component:** `AlgorandGoogleDriveAccount/BusinessLogic/JwtIssuerService.cs:475-487`.
- **Current mitigations:** Issuer and lifetime are still validated; the client/redirect-URI allowlist limits who
  can obtain a token in the first place.
- **Recommended further mitigation:** Enable `ValidateAudience` with the correct expected audience per resource.
- **Status:** Open.
- **History:**
  - 2026-23-07 — claude-code-ai-review: opened at 10%, corresponds to finding F-03. Likelihood expected to rise as
    the number of distinct integrated OIDC clients grows — revisit at next audit.

### R-004 — MCP fund-transfer tools broadcast on-chain transactions with no server-side confirmation gate

- **Description:** `TransferAsset`/`OptIn` MCP tools sign and broadcast immediately based solely on the (weak,
  see R-001) session ID, with no confirmation step, spending limit, or receiver allowlist enforced server-side —
  exposing both a direct extension of R-001 and an independent prompt-injection-to-theft path for legitimately
  paired users whose AI agent processes untrusted content.
- **Likelihood (5-year misuse probability):** 25% — reasoning: prompt injection against tool-using AI agents is a
  well-documented, actively exploited class of attack across the industry (not hypothetical), and this system
  provides a directly monetizable target (irreversible on-chain fund transfer) with no server-side circuit
  breaker — the likelihood is driven primarily by this being a known, broadly-attacked pattern rather than by any
  Biatec-specific evidence.
- **Impact:** Direct, irreversible loss of on-chain funds for the affected user(s); combined with R-001 this
  extends to any user whose pairing session is compromised, not just users who personally fall for a prompt
  injection.
- **Affected component:** `AlgorandGoogleDriveAccount/MCP/BiatecMCPGoogle.cs`.
- **Current mitigations:** None at the server/tool level; entirely dependent on external MCP client/host trust
  boundaries and user vigilance.
- **Recommended further mitigation:** Server-enforced spending limits and/or receiver allowlisting; a separate,
  freshly issued confirmation token for fund-moving calls, independent of the long-lived pairing session.
- **Status:** Open.
- **History:**
  - 2026-23-07 — claude-code-ai-review: opened at 25%, corresponds to finding F-04.

### R-005 — Authorization-code / pending-authorize-request redemption is not atomic

- **Description:** Redis get-then-delete for one-time codes and pending authorize requests is not atomic,
  permitting a narrow-window double-redemption race.
- **Likelihood (5-year misuse probability):** 5% — reasoning: requires precise timing against a very short-lived
  (120-second) code with no other exploitable gap (PKCE/redirect-URI/client checks are unaffected) — a
  low-value, high-effort attack for limited gain.
- **Impact:** At most, an authorization code redeemed twice, yielding a duplicate token set to a racing requester;
  bounded by the same PKCE and redirect-URI checks as normal exchange.
- **Affected component:** `AlgorandGoogleDriveAccount/BusinessLogic/JwtIssuerService.cs`.
- **Current mitigations:** Very short code lifetime (120s) narrows the practical race window.
- **Recommended further mitigation:** Atomic `GETDEL` or Lua-scripted get-and-delete.
- **Status:** Open.
- **History:**
  - 2026-23-07 — claude-code-ai-review: opened at 5%, corresponds to finding F-05.

### R-006 — Non-constant-time client-secret comparison

- **Description:** `ValidateClientAuthentication` compares client secrets with `string.Equals(...,
  StringComparison.Ordinal)`, a short-circuiting, non-constant-time comparison.
- **Likelihood (5-year misuse probability):** 3% — reasoning: remote timing attacks against a real network path
  (as opposed to a local/co-located attacker) require very high measurement precision and many samples; practical
  exploitation over the internet against this endpoint is difficult though not impossible.
- **Impact:** Theoretical confidential-client secret recovery; would allow impersonating a whitelisted third-party
  client at the token endpoint.
- **Affected component:** `AlgorandGoogleDriveAccount/BusinessLogic/JwtIssuerService.cs:677`.
- **Current mitigations:** None beyond network jitter naturally degrading timing-attack precision.
- **Recommended further mitigation:** `CryptographicOperations.FixedTimeEquals`.
- **Status:** Open.
- **History:**
  - 2026-23-07 — claude-code-ai-review: opened at 3%, corresponds to finding F-06.

### R-007 — Silent fallback to an ephemeral JWT signing key on configuration failure

- **Description:** Missing/misconfigured `JwtIssuer:SigningPrivateKeyPem` degrades to an in-memory ephemeral RSA
  key with only a warning log, rather than failing startup.
- **Likelihood (5-year misuse probability):** 8% (as an *operational* incident, not primarily an attacker-driven
  misuse) — reasoning: this is most likely to manifest as a self-inflicted configuration/ops incident (a botched
  secret rotation or deployment) rather than something an external attacker directly triggers, but it is included
  here because such incidents recur periodically in any team's operational history over a 5-year horizon, and the
  security consequence (silent, low-visibility key change / multi-replica JWKS mismatch) is real when it happens.
- **Impact:** Service-wide token invalidation and/or cross-replica validation failures without an operator being
  clearly alerted; secondary security angle if an attacker can deliberately induce the config failure.
- **Affected component:** `AlgorandGoogleDriveAccount/BusinessLogic/JwtIssuerService.cs`.
- **Current mitigations:** A `LogWarning` is emitted (visible to anyone actively monitoring logs).
- **Recommended further mitigation:** Fail fast at startup in non-Development environments.
- **Status:** Open.
- **History:**
  - 2026-23-07 — claude-code-ai-review: opened at 8%, corresponds to finding F-07.

### R-008 — Open redirect on `/api/drive/login` and `/api/drive/logout`

- **Description:** `redirectUri` query parameter on both actions is passed unvalidated into
  `AuthenticationProperties.RedirectUri`, unlike the allowlisted OIDC redirect flows.
- **Likelihood (5-year misuse probability):** 12% — reasoning: open redirects immediately following a real
  authentication step are a known, low-cost phishing primitive that requires no special access to exploit —
  just crafting a link — making it more attacker-accessible than most other findings in this registry, even
  though the direct impact per use is lower.
- **Impact:** Phishing-enablement (a link that authenticates via this service's genuine trusted domain before
  redirecting to an attacker page); does not directly leak a token or secret on its own.
- **Affected component:** `AlgorandGoogleDriveAccount/Controllers/DriveController.cs:112-130`.
- **Current mitigations:** None.
- **Recommended further mitigation:** Validate against the existing `RedirectUriMatcher` allowlist mechanism.
- **Status:** Open.
- **History:**
  - 2026-23-07 — claude-code-ai-review: opened at 12%, corresponds to finding F-08.

### R-009 — Cross-Account Protection disabled by default; `email_verified` not enforced at runtime by default

- **Description:** RISC-style checks (including `email_verified`) never run unless `CrossAccountProtection:Enabled`
  is explicitly turned on, which it is not by default.
- **Likelihood (5-year misuse probability):** 6% — reasoning: exploitation requires an attacker to obtain a Google
  OIDC token with `email_verified=false` that is nonetheless accepted upstream by the base ASP.NET Core OIDC
  handler — Google's own account-creation flow makes unverified-email OAuth tokens uncommon in practice, so this
  is a defense-in-depth gap rather than a directly exploitable hole today.
- **Impact:** Weakens the runtime assurance behind the identity claim that the entire tenant-isolation model
  (R-002) depends on.
- **Affected component:** `AlgorandGoogleDriveAccount/Model/Configuration.cs`;
  `AlgorandGoogleDriveAccount/BusinessLogic/CrossAccountProtectionService.cs`.
- **Current mitigations:** Base ASP.NET Core Google OIDC handler still validates `id_token` signature/audience/
  issuer independent of this feature.
- **Recommended further mitigation:** Enforce `email_verified` unconditionally at the OIDC handler level,
  independent of the optional CAP feature toggle.
- **Status:** Open.
- **History:**
  - 2026-23-07 — claude-code-ai-review: opened at 6%, corresponds to finding F-09.

### R-010 — `HasScopeAsync` does not check the actual granted scope

- **Description:** Returns `true` for any non-empty access token regardless of the `scope` parameter requested.
- **Likelihood (5-year misuse probability):** 4% — reasoning: this is a logic bug affecting client-side
  authorization *decisions* built on an unreliable signal, not a direct bypass of Google's own server-side scope
  enforcement on Drive API calls — misuse would look like a confusing UX/authorization-decision bug surfacing
  during future feature development more than an external attack.
- **Impact:** Incorrect "do we have access" gating in any feature built on top of this method; Google's own API
  still enforces real scopes.
- **Affected component:** `AlgorandGoogleDriveAccount/BusinessLogic/GoogleAuthorizationService.cs:60-91`.
- **Current mitigations:** Google's server-side scope enforcement on actual Drive API calls.
- **Recommended further mitigation:** Compare requested scope against the token's actual granted scopes.
- **Status:** Open.
- **History:**
  - 2026-23-07 — claude-code-ai-review: opened at 4%, corresponds to finding F-10.

### R-011 — Verbose internal error messages surfaced in API responses

- **Description:** Decryption and other internal exception messages (email, file size, raw exception text) are
  returned directly in HTTP response bodies across several controllers.
- **Likelihood (5-year misuse probability):** 15% — reasoning: information-disclosure-via-verbose-errors is
  routinely found and used for reconnaissance in real-world attacks against APIs of this shape; it is easy to
  trigger and requires no special access, though its direct impact is reconnaissance-level rather than a
  standalone compromise.
- **Impact:** Confirms email existence, discloses internal implementation details useful for chaining with other
  findings (notably as a padding-oracle amplifier for R-002).
- **Affected component:** `AlgorandGoogleDriveAccount/Repository/GoogleDriveRepository.cs`;
  `DriveController.cs`; `DevicePairingController.cs`.
- **Current mitigations:** None.
- **Recommended further mitigation:** Return generic error messages to API callers; log details server-side only.
- **Status:** Open.
- **History:**
  - 2026-23-07 — claude-code-ai-review: opened at 15%, corresponds to finding F-11.

### R-012 — Drive search-query built via unescaped string interpolation (not currently attacker-reachable)

- **Description:** `folderRequest.Q` interpolates configuration-sourced values without escaping; not reachable by
  user input today.
- **Likelihood (5-year misuse probability):** 2% — reasoning: requires a future code change that routes user
  input into this query construction before it becomes exploitable; not exploitable in the current codebase.
- **Impact:** Would be a Drive search-query injection if ever fed user-controlled input.
- **Affected component:** `AlgorandGoogleDriveAccount/Repository/GoogleDriveRepository.cs`.
- **Current mitigations:** Only configuration-sourced values reach this code path today.
- **Recommended further mitigation:** Escape/parameterize as defensive coding practice regardless of current
  reachability.
- **Status:** Open.
- **History:**
  - 2026-23-07 — claude-code-ai-review: opened at 2%, corresponds to finding F-12.

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
  contents for secret material.
- **Status:** Open.
- **History:**
  - 2026-23-07 — claude-code-ai-review: opened at 10%, corresponds to finding F-13.

### R-014 — `id_token_hint` audience trusted without signature validation at `/connect/endsession`

- **Description:** `TryGetClientIdFromIdTokenHint` reads `aud` from an unvalidated JWT solely to pick which
  registered client's allowlist to check the real redirect target against.
- **Likelihood (5-year misuse probability):** 3% — reasoning: bounded impact (can only steer to another
  legitimately registered client's allowlist, not escape allowlisting entirely) makes this a low-value target for
  an attacker.
- **Impact:** Logout redirected to a URI on a different, but still legitimately registered, client's allowlist.
- **Affected component:** `AlgorandGoogleDriveAccount/BusinessLogic/JwtIssuerService.cs`.
- **Current mitigations:** Final redirect target still validated against a real, registered allowlist.
- **Recommended further mitigation:** Validate `id_token_hint` signature before trusting `aud`.
- **Status:** Open.
- **History:**
  - 2026-23-07 — claude-code-ai-review: opened at 3%, corresponds to finding F-14.

_(Note: R-015 is intentionally not used. Finding F-15 in the audit report — review of `RedirectUriMatcher` — found
no bypass and is a confirmation of a control working correctly, not a risk; it is documented in the report but has
no corresponding registry entry.)_

### R-016 — Possible captive-dependency between singleton `GoogleDriveRepository` and `IGoogleAuthProvider` (unconfirmed)

- **Description:** If `IGoogleAuthProvider` is Scoped-lifetime in `Google.Apis.Auth.AspNetCore3`, its injection
  into the singleton `GoogleDriveRepository` could capture the first request's credential for all subsequent
  requests using the implicit-credential code path (`/api/drive/sign`, `/api/drive/address`). Not confirmed by
  this audit — see finding F-16 for full reasoning and required follow-up.
- **Likelihood (5-year misuse probability):** Not estimated — status is "needs verification," not yet a scored
  risk. If confirmed present, likelihood of *accidental* trigger (not even requiring an attacker — any two
  concurrent users on the affected endpoints) would be high; this entry exists to ensure the question is not
  dropped before the next audit resolves it.
- **Impact:** If confirmed: cross-user credential confusion — one user's requests silently operating against
  another user's Google Drive/account. Would be re-classified Critical if confirmed.
- **Affected component:** `AlgorandGoogleDriveAccount/Repository/GoogleDriveRepository.cs`.
- **Current mitigations:** None known; unconfirmed.
- **Recommended further mitigation:** Confirm `IGoogleAuthProvider`'s DI lifetime (source/decompile check or
  concurrent-request integration test); if Scoped, either re-architect the credential-passing so the singleton
  never depends on ambient per-request state, or change `GoogleDriveRepository`'s own lifetime.
- **Status:** Open (verification required before this can be scored/closed).
- **History:**
  - 2026-23-07 — claude-code-ai-review: opened as unconfirmed, corresponds to finding F-16.

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

## Closed risks

_None closed yet — this is the first audit._
