# Deep Security Audit — Instructions for Auditors

## Purpose

The Biatec MCP Server (this repository, `AlgorandGoogleDriveAccount`) implements a self-custody model in which
Algorand private keys are AES-256 encrypted, bound to the user's email address, and stored **only in the user's own
Google Drive** — never on Biatec's servers — plus an OpenID Connect identity provider that issues Algorand-identity
claims to whitelisted third-party applications.

These audits exist to give end users, integrating relying parties, and other stakeholders independent, professional
assurance that:

- keys held in the user's Google Drive cannot be recovered, decrypted, or exfiltrated by Biatec, by an attacker who
  compromises Biatec's infrastructure, or by an attacker who compromises the user's Google Drive alone (without also
  compromising the user's email/identity);
- the OIDC/JWT issuer cannot be tricked into asserting a false identity, issuing tokens to unauthorized clients, or
  leaking tokens/keys via redirect URI or session handling flaws;
- the overall architecture matches what is documented and publicly claimed.

Each engagement is expected to represent a **deep audit with an estimated cost over $100,000** — i.e. full
whitebox source review, architecture/threat-modeling, cryptographic review, dependency and infrastructure review,
and adversarial testing, not a superficial scan. Findings must be defensible to a technically sophisticated,
skeptical external reader (a user deciding whether to trust this system with real funds).

## Scope

In scope, at minimum:

- `AlgorandGoogleDriveAccount/BusinessLogic/` — all services, especially `DriveService`, `DevicePairingService`,
  `GoogleAuthorizationService`, `CrossAccountProtectionService`, `JwtIssuerService`, `PortfolioValuationService`.
- `AlgorandGoogleDriveAccount/Controllers/` — `DevicePairingController`, `DriveController`, `JwtIssuerController`.
- `AlgorandGoogleDriveAccount/Helper/AesEncryptionHelper.cs` and `RedirectUriMatcher.cs` — treat as the two most
  security-critical files in the codebase.
- `AlgorandGoogleDriveAccount/Repository/GoogleDriveRepository.cs` — the only singleton service; review for
  cross-request state leakage in addition to Drive API correctness.
- `AlgorandGoogleDriveAccount/MCP/BiatecMCPGoogle.cs` — MCP tool surface exposed to AI clients; review for
  prompt-injection-adjacent risk (an AI client being induced to invoke a signing/export tool it should not).
- Configuration surfaces bound from `appsettings.json` (`Model/*.cs`), especially `JwtIssuer:Clients` allowlisting
  and `AesOptions`.
- `.github/workflows/build-api.yml` and `docs/KUBE_CONFIG_SECURITY.md` — CI/CD pipeline and the scope/lifetime of
  the deployment credentials it uses, since a compromised pipeline can bypass all application-layer controls.
- Any third-party dependency with a direct line to key material, token issuance, or Drive access (transitive
  dependency review, known CVEs, supply-chain pinning).

Out of scope unless explicitly extended by the engagement letter: load/performance testing, non-security code
quality, UI/UX review of `wwwroot/*.html` beyond the security-relevant flows (pairing, consent).

## Independence and conduct

- Auditors must be independent of the development of the feature/commit being audited. Do not audit your own code
  changes.
- Disclose any conflict of interest (employment, equity, prior consulting relationship with Biatec / Scholtz &
  Company, j.s.a.) in the report's front matter.
- All testing against the running system (not just static source review) must be authorized in advance in writing
  by the engagement owner and scoped to non-production data. Do not perform any testing that could affect real user
  funds, real user Google Drive contents, or production availability without explicit written authorization for
  that specific action.
- Findings must be reproducible: include exact steps, inputs, and code references (`file:line`) for every claim.

## Report requirements

### Filename

Each audit produces exactly one report file in this folder, named:

```
audit-report-{yyyy-mm-dd}-{git-short-tag}-{auditor-signature}.md
```

- `{yyyy-mm-dd}` — the date the audit report is finalized, e.g. `2026-07-23` for 23 July 2026. ISO 8601 order
  (year-month-day), so a plain alphabetical listing of this folder is also chronological. Reports written before
  2026-08-04 used a year-day-month order and were renamed to this convention; do not reintroduce it.
- `{git-short-tag}` — the short commit hash (`git rev-parse --short HEAD`) of the exact commit audited. If the audit
  covers a range of commits (e.g. a full annual re-audit), use the hash of the final commit in scope and state the
  full range in the report body.
- `{auditor-signature}` — a stable identifier for the auditor or audit firm (e.g. firm name or handle, lowercase,
  hyphenated). This must match the signature used in the risk registry (see below) so entries can be traced back to
  who raised them.

Example: `audit-report-2026-07-23-a1b2c3d-northshore-security.md`

### Structure

Every report must include, in this order:

1. **Front matter** — auditor/firm name, conflict-of-interest disclosure, commit(s) audited, dates of engagement,
   scope statement (link back to the Scope section above and note any deviations), methodology summary, and overall
   verdict (e.g. pass / pass-with-findings / fail — define these terms explicitly in the report since there is no
   fixed rubric across firms).
2. **Executive summary** — written for a non-technical user deciding whether to trust the system with funds.
   Plain language, no unexplained jargon, 1 page maximum.
3. **Methodology** — what was reviewed, how (static review, dynamic testing, dependency scanning, cryptographic
   analysis, etc.), and what tools were used.
4. **Detailed findings** — one entry per finding, each with: severity (Critical / High / Medium / Low /
   Informational), affected component (`file:line`), description, proof of concept or reproduction steps, impact,
   and recommended remediation. Include findings that were investigated and found to be non-issues if a reasonable
   reader would otherwise wonder about them (e.g. "we specifically checked X for Y and found it is not exploitable
   because Z").
5. **Remediation tracking** — for any finding that references a prior audit's unresolved item, state explicitly
   whether it has been fixed, partially fixed, or remains open.
6. **Risk registry changes** — a summary of what this audit added, revised, or closed in `RISKS.md`, with
   justification for each change (see below).
7. **Signature** — auditor/firm name and, where applicable, a cryptographic signature or attestation over the report
   file's hash, so users can verify the report was not altered after publication.

Reports must be written to a professional publication standard: precise, unambiguous, free of marketing language,
and structured so that a reader can independently verify every material claim against the codebase.

## Risk registry (`audits/RISKS.md`)

`RISKS.md` is the living, cumulative risk registry for this system. It is not per-audit — it is revised in place by
each successive audit.

**Auditors are responsible for managing this file** as part of every engagement:

- **Create it** if it does not yet exist (first audit).
- **Add** new risks discovered during this engagement.
- **Revise** the likelihood estimate of existing risks if this engagement's findings changed the picture (e.g. a
  mitigating control was added or removed, a new dependency changed the attack surface).
- **Close** risks that have been fully mitigated — move them to a "Closed risks" section with the closing audit's
  signature and date, rather than deleting them, so the historical record is preserved.
- **Explicitly list risks that cannot be mitigated** given the current architecture (e.g. "a user who loses both
  their Google account and their recovery mechanism loses their funds permanently — this is inherent to the
  self-custody model and is not a defect"). These belong in a dedicated "Accepted / unmitigable risks" section and
  must not be silently dropped by a later audit without justification.

### Required fields per risk entry

Each risk entry must record:

- **ID** — a stable identifier (e.g. `R-001`), never reused even if the risk is later closed.
- **Title** — short description.
- **Description** — the risk in full: what could go wrong, and what threat actor/scenario triggers it.
- **Likelihood (0–100%)** — the estimated probability that this risk **will be misused/exploited within the next 5
  years**, given the system as it exists today. State the reasoning behind the number, not just the number itself —
  a bare percentage is not auditable. Reasoning should reference realistic threat actors, known incidents in
  comparable systems, and current mitigations in place.
- **Impact** — what happens if it is misused (e.g. loss of funds for N users, identity spoofing, full key
  exfiltration).
- **Affected component** — file/module reference.
- **Current mitigations** — what already reduces likelihood or impact.
- **Recommended further mitigation** — if any; state "none identified" if the risk is accepted as-is.
- **Status** — Open / Mitigated / Accepted (unmitigable) / Closed.
- **History** — one line per audit that touched this entry: date, auditor signature, and what changed
  (opened / likelihood revised from X% to Y% / mitigation added / closed) and why.

### Likelihood estimation discipline

The 0–100% figure is a 5-year misuse likelihood, not a severity or CVSS score. Two risks with identical impact can
have very different likelihoods depending on how exposed and how attractive they are to attackers. When revising a
likelihood estimate, an auditor must explain what changed since the last estimate — a number should never be
adjusted without a stated reason, since the registry's value is in showing users the trend over time, not just a
current snapshot.

## Cadence

An audit under this process should be performed:

- before any material change to `AesEncryptionHelper.cs`, `RedirectUriMatcher.cs`, the OIDC token-issuance flow, or
  the device-pairing trust boundary ships to production;
- at least annually regardless of code changes, to re-assess likelihood estimates in `RISKS.md` against the current
  threat landscape (new CVEs, new attack techniques against OAuth/OIDC or Google Drive integrations, etc.);
- after any security incident affecting this system or a direct dependency (e.g. a Google Drive API or
  `Microsoft.IdentityModel.Tokens` vulnerability).

## Publication

Audit reports and the risk registry are intended to be shared with end users and integrating third parties as
evidence of due diligence. Do not include in a report anything that would itself constitute a security risk if
published (e.g. unpatched exploit details for a vulnerability that has not yet been fixed) — coordinate disclosure
timing with the engagement owner before publishing a report that describes an unresolved Critical/High finding.
