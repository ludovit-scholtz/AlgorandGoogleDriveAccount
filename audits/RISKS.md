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

**2026-08-01 third audit note:** A third engagement
([audit-report-2026-01-08-69d410c-claude-code-ai-review-3.md](audit-report-2026-01-08-69d410c-claude-code-ai-review-3.md),
signature `claude-code-ai-review-3`) reviewed the substantial new surface area added since the second audit — a
full three-way project restructure (`BiatecSelfCustodyCore`/`BiatecMCP`/`BiatecOIDC`), Microsoft Entra ID/OneDrive
as a second storage provider, a multi-seed vault with on-chain rekey support, an AES key-ring rotation mechanism,
cached/refreshable provider access tokens embedded in issued OIDC tokens, and a new cross-cloud vault-backup
feature. This pass was narrower than the second audit's full line-by-line re-verification of every prior finding
(see that report's §5 caveat) and instead prioritized the genuinely new code. It found one new High finding
(R-020, an OAuth CSRF gap in the new vault-backup flow), two new Medium findings (R-021, a vault-file race
condition; R-022, a known dependency advisory), confirmed R-018 and R-019 unchanged, extended R-019's concern to
a second key ring (`ProviderTokenProtection` — see that entry's history), and found a genuine, unprompted
improvement to R-013 (production deploys are no longer automatic on every push to `master`).

**2026-08-02 remediation note:** Engineering (not a new independent audit) implemented fixes for every finding the
third audit raised — R-020, R-021, R-022, R-018, and R-019/R-023 — in the same commit series that added this note.
Each remediated entry's History records what changed, why, and any residual scope/limitation the fix does not
claim to fully close. As with the 2026-07-24 remediation note, this is first-party engineering work responding to
audit findings — it does **not** constitute a new independent audit and does not itself re-verify the absence of
new defects introduced by the fix. Per `AUDITS-INSTRUCTIONS.md`'s cadence rule ("before any material change to
`AesEncryptionHelper.cs`... ships to production"), and because this remediation pass touches
`CloudAccountRepository.cs`/`AesKeyRingResolver.cs` (security-critical per that same rule) and introduces a new
authorization check on a production-facing OAuth flow (H-01/R-020), an independent audit engagement should
re-verify all of the below before this registry's updated "Closed"/"Mitigated" statuses are relied upon as
external assurance — the same standing recommendation every prior remediation pass and audit in this registry has
carried. All 377 existing automated tests (107 in `BiatecMCPTests`, 270 in `BiatecOIDCTests`) still pass after this
remediation pass; no new regression tests were added for the new checks themselves (see individual entries for
what a future audit should specifically verify).

**2026-08-04 fourth audit note:** A fourth engagement
([audit-report-2026-04-08-34459ac-claude-code-ai-review-4.md](audit-report-2026-04-08-34459ac-claude-code-ai-review-4.md),
signature `claude-code-ai-review-4`, same reviewing party/method and the same non-independence caveat as the
first three — see that report's §1.2) reviewed the ~23,700 lines added since the third audit (`69d410c..34459ac`):
EVM transaction signing, Bitcoin/Bitcoin Cash support, the address-centric wallet API and address-activation
registry, multisig cosigning tools, the strict network-code resolver, the Aramid bridge integration, and a
test-only mock identity/storage provider. It prioritized the *value-moving* surface — transaction inspection
and spending-limit enforcement across all four chain families now supported — over breadth (see that report's
§1.5 for what was **not** covered).

Its central conclusion: **the daily/weekly/monthly spending limit, the only control bounding what a
legitimately-authorized but hostile or compromised relying party can take, does not hold across most of the
newly-supported surface.** Two High findings (R-024, an Algorand `close-remainder-to` sweep priced at zero —
proof-of-concept executed; R-025, a Bitcoin-family bypass via a caller-supplied `isChange` flag) and three
Medium findings (R-026 EVM entirely unmetered, R-027 non-mainnet AVM chains skipped, R-028 application calls
unpriced) are all instances of the same shape: a value-moving construct the valuation code never reads. Two
further findings concern the address-activation registry (R-029) and a configuration-gated authentication
bypass shipping in the production artifact (R-030). No finding permits key exfiltration or identity forgery,
and the core self-custody guarantee was found intact — verdict "pass with findings".

The audit also re-verified every item the third audit and the 2026-08-02 remediation pass touched, against
the code rather than against the remediation pass's own description: R-020, R-018, R-019/R-023 and R-022 were
all confirmed genuinely fixed; R-021's fix was confirmed for the seed vault but found **not** to extend to
`AddressActivationService` (tracked as R-029, not by re-opening R-021 — the precedent set for R-011/R-018 by
the second audit). R-013 is unchanged. Test baseline at time of audit: 700 tests passing (405 + 295), zero
vulnerable dependencies.

## Open risks

### R-024 — Spending limit completely bypassed by a `close-remainder-to` payment (Algorand mainnet)

- **Description:** `AlgorandTransactionInspector.Inspect` values a payment by reading exactly one wire field,
  `amt`. Algorand payments carry a second, independent value-moving field, `close` (`CloseRemainderTo`), which
  transfers the sender's **entire remaining balance** to a named address; `amt` may be zero. The inspector never
  reads it, so `WalletService` prices such a transaction at $0.00, the `if (totalUsd > 0m)` guard skips
  `EnsureWithinLimitsAsync` entirely, no ledger entry is written, and the transaction is signed. Neither adjacent
  control helps: the `sender_mismatch` check passes (the sender genuinely is the user's own address) and the
  `rekey` claim gate does not fire (`close` is not `rekey`). The endpoint accepts caller-supplied base64 msgpack
  directly, so "our own builder never sets `close`" is not a control. **Threat actor:** any relying party holding
  a validly-issued `sign`-scoped token that turns hostile or is compromised — precisely the adversary the
  spending limit exists to bound. The MCP surface deliberately extends signing to third-party AI agents, widening
  this set.
- **Likelihood (5-year misuse probability):** 45% — reasoning: exploitation requires no privileged network
  position, no cryptographic weakness, and no unusual protocol knowledge — one extra field on a request the API
  already accepts. A proof of concept was executed against this commit and confirmed both halves (priced at zero;
  the `close` field survives the decode/sign/re-encode round trip, so the returned signature is usable on-chain).
  Close-out sweeps are a standard, well-documented Algorand feature that any competent integrator or agent knows.
  Held below 50% only because it requires the RP itself to be hostile or compromised rather than an outside
  attacker, and Biatec's relying-party population is currently small and largely first-party; this number should
  rise materially as third-party integrations grow.
- **Impact:** Total loss of the ALGO balance of any address a hostile `sign`-scoped relying party can name, in a
  single request, regardless of the user's configured limits — and the user has been explicitly told a limit
  applies. Because the whole balance moves in one transaction, the rolling daily/weekly/monthly windows provide no
  partial protection either.
- **Affected component:** `BiatecOIDC/Helper/AlgorandTransactionInspector.cs:122-127` (and the key constants at
  :67-71); `BiatecOIDC/BusinessLogic/WalletService.cs:69-95`.
- **Current mitigations:** None effective. The `sign` claim itself is the only gate, and it is exactly the gate
  the threat actor legitimately holds. The asset-transfer analogue (`aclose`/`AssetCloseTo`) is **not** currently
  exploitable, but only because the pinned `Algorand4` 4.4.1 `AssetTransferTransaction` type does not model that
  property, so the field is dropped when `DriveService` re-encodes — an accident of the SDK's object model, not a
  control, which would regress silently on an SDK upgrade.
- **Recommended further mitigation:** Read the `close` key in the inspector and **fail closed** rather than trying
  to price it (the swept amount depends on the account's live balance and is not knowable from the transaction
  alone): surface an `IsCloseOut` flag and reject any `pay` carrying `close` unless the token holds a dedicated
  high-privilege claim, following the `rekey` claim's precedent for permanently-destructive operations. Apply the
  same treatment to `aclose` for defense in depth. Add regression tests for both.
- **Status:** Open.
- **History:**
  - 2026-08-04 — claude-code-ai-review-4: opened at 45%, corresponds to finding H-01 in
    [audit-report-2026-04-08-34459ac-claude-code-ai-review-4.md](audit-report-2026-04-08-34459ac-claude-code-ai-review-4.md).
    Also records that audit's process finding (M-05, not tracked as its own entry): none of the 700 tests in the
    suite exercises any spending-limit bypass class — the inspector's 19 test cases all ask "does it correctly
    price what it knows about", never "what can move value without it noticing", which is why four chain families
    were added without the gap being caught. Remediation for this entry should include negative tests per chain
    family.

### R-025 — Bitcoin/Bitcoin Cash spending limit bypassed by a caller-supplied `IsChange` flag; unbounded implicit fee

- **Description:** Bitcoin-family spend value is computed as the sum of outputs where `!o.IsChange`. `IsChange`
  is a plain boolean on the wire DTO, deserialized straight from the caller's request body; nothing verifies that
  an output marked as change actually pays an address the signer controls. `DriveService` builds each output from
  the caller's `Address` with no comparison against the signer's own derived address (which it has already
  computed, and correctly uses to reconstruct the *inputs'* scriptPubKeys). A hostile caller therefore marks its
  own payout `"isChange": true` and the whole transfer prices at $0. Independently, the implicit miner fee is
  `sum(Inputs) − sum(Outputs)` with no upper bound, so the entire UTXO set can be burned to fee against a
  1-satoshi output — destructive without any collusion, and capturable by a colluding miner.
- **Likelihood (5-year misuse probability):** 30% — reasoning: the exploit shape is identical in difficulty to
  R-024 (set one field), so the difference is exposure, not difficulty. Rated lower because Bitcoin/Bitcoin Cash
  support is new, has never been verified against a live node or mempool (the repository's own documented
  caveat), and therefore currently holds little real balance. This estimate should be revised sharply upward the
  moment BTC/BCH transfers are used with real funds.
- **Impact:** Total loss of the BTC/BCH balance of any address a hostile `sign`-scoped relying party can name,
  regardless of configured limits; plus unbounded destructive fee burn.
- **Affected component:** `BiatecSelfCustodyCore/Model/BitcoinUnsignedTransaction.cs:41-47`;
  `BiatecOIDC/BusinessLogic/WalletService.cs:151`; `BiatecSelfCustodyCore/BusinessLogic/DriveService.cs:186-190`.
- **Current mitigations:** None for either half. Note the same file makes the *correct* trust decision one field
  over — input scriptPubKeys are deliberately reconstructed from the signer's own address rather than trusted
  from the wire — so the pattern to follow already exists in the same method.
- **Recommended further mitigation:** Ignore the caller's `IsChange` at the enforcement boundary entirely; derive
  the signer's own Bitcoin/Bitcoin Cash address and treat an output as change only if its `Address` matches, with
  everything else counted as spend. Price the implicit fee as spend, or reject a fee exceeding a sane multiple of
  the size-estimated fee. Consider removing `IsChange` from the wire DTO altogether — a field whose only consumer
  is a security decision, and whose value the server can compute itself, should not be caller-supplied.
- **Status:** Open.
- **History:**
  - 2026-08-04 — claude-code-ai-review-4: opened at 30%, corresponds to finding H-02 in
    [audit-report-2026-04-08-34459ac-claude-code-ai-review-4.md](audit-report-2026-04-08-34459ac-claude-code-ai-review-4.md).

### R-026 — EVM signing has no spending-limit enforcement of any kind

- **Description:** `WalletService.SignEvmTransactionGroupAsync` goes straight from argument validation to signing
  — no valuation, no limit check, no ledger entry — so a `sign`-scoped token can move unlimited native value (and
  execute unlimited ERC-20 `transfer` calldata) on Ethereum, Gnosis, Arbitrum and Base. The gap is documented in
  `chains.html`'s capability matrix and in the method's own remarks, but it is **not** surfaced where the user
  configures a limit: `PUT /wallet/limits` accepts and stores a limit with no indication it does not apply to
  half the supported chains, and `GET /wallet/limits` reports it back unqualified. A user who sets a $100/day
  limit reasonably believes it constrains their EVM addresses.
- **Likelihood (5-year misuse probability):** 35% — reasoning: no exploitation technique is needed at all; the
  control simply is not there, so any hostile `sign`-scoped RP transferring EVM value is "exploiting" it by
  default. The EVM chains supported are the most liquid of any family here, which raises attractiveness. Held
  below R-024 because this is a known, documented gap that an attentive integrator or user could in principle
  discover before relying on it, rather than a silent failure of a control that appears to be working.
- **Impact:** Unlimited loss of native-token balances on four EVM chains for a hostile `sign`-scoped relying
  party; and, more broadly, a limits API that reports protection stronger than what is actually in force.
- **Affected component:** `BiatecOIDC/BusinessLogic/WalletService.cs:113-133`;
  `BiatecOIDC/Controllers/WalletController.cs:271-325`.
- **Current mitigations:** Documentation only (`chains.html` capability matrix, method remarks). No runtime
  control.
- **Recommended further mitigation:** Implement EVM valuation (native-token price oracle plus calldata-aware
  ERC-20 decoding). As an immediate, cheap interim step, have both limits endpoints return an explicit
  `enforcedOn`/`notEnforcedOn` chain list so the API stops overstating the protection in force, and document the
  gap in the integration guide's limits section rather than only in the capability matrix.
- **Status:** Open.
- **History:**
  - 2026-08-04 — claude-code-ai-review-4: opened at 35%, corresponds to finding M-01 in
    [audit-report-2026-04-08-34459ac-claude-code-ai-review-4.md](audit-report-2026-04-08-34459ac-claude-code-ai-review-4.md).

### R-027 — Spending limits silently skipped on every AVM chain except Algorand mainnet, including real-value chains

- **Description:** `WalletController.SignTransactionGroup` computes `isAlgorandMainnet` from the resolved
  network's genesis id and passes it as `applySpendingLimits`; when false, the entire pricing/limit block in
  `WalletService` is skipped. The rationale is sound as far as it goes — the Biatec Router prices assets only on
  Algorand mainnet, and applying it elsewhere previously failed every transfer closed with a confusing
  valuation error (a real reported bug this change fixed). The problem is the fallback *direction*: Voi mainnet
  and Aramid mainnet are production chains carrying real value and are in the supported network list, so on those
  chains a configured limit is not merely unenforceable but silently ignored, with a 200 response
  indistinguishable from an enforced one.
- **Likelihood (5-year misuse probability):** 20% — reasoning: same "no technique required" character as R-026,
  but with materially smaller balances and a much smaller relying-party population on Voi/Aramid than on Algorand
  mainnet or the EVM chains. The behavior was introduced within the audited range (commit `c5964ba`), so it has
  had little time in production.
- **Impact:** A hostile `sign`-scoped relying party faces no spending limit on Voi or Aramid mainnet, and the user
  has no way to discover this from the API.
- **Affected component:** `BiatecOIDC/Controllers/WalletController.cs:222`;
  `BiatecOIDC/BusinessLogic/WalletService.cs:67-96`.
- **Current mitigations:** None at runtime; documented in `CLAUDE.md` and the integration guide's AVM signing
  section as a deliberate, known gap.
- **Recommended further mitigation:** Distinguish "no value at risk" (testnets — safe to skip) from "value at risk
  but unpriceable" (Voi, Aramid — should not silently skip). For the latter, either reject with a clear
  `limits_unenforceable_on_network` error when the account has a non-zero limit configured, or require a
  per-account opt-in acknowledging the gap. At minimum, surface the affected chains in the limits response as
  recommended for R-026.
- **Status:** Open.
- **History:**
  - 2026-08-04 — claude-code-ai-review-4: opened at 20%, corresponds to finding M-02 in
    [audit-report-2026-04-08-34459ac-claude-code-ai-review-4.md](audit-report-2026-04-08-34459ac-claude-code-ai-review-4.md).

### R-028 — Application calls and asset-config transactions are unpriced; inner transactions escape the spending limit

- **Description:** Every Algorand transaction type other than `pay`/`axfer` maps to
  `AlgorandTransactionKind.Other` and is priced at zero. That includes `appl` (application call), which on
  Algorand can emit **inner transactions** moving arbitrary amounts of ALGO and any ASA out of the caller's
  account — the mechanism behind every DeFi interaction on the chain. It also includes `acfg`, which can reassign
  an asset's clawback/manager addresses, enabling a later drain that never passes through this endpoint at all.
  A hostile relying party does not need R-024's protocol trick if it can deploy and call its own application.
- **Likelihood (5-year misuse probability):** 40% — reasoning: this is the most *general* of the bypasses and
  requires no protocol subtlety whatsoever — any contract call. Rated slightly below R-024 because the framing is
  genuinely ambiguous: many users interacting with DeFi through Biatec would consider app calls to be intended,
  wanted behavior rather than a bypass, and unlike R-024 there is no clean fix (the value moved by an app call is
  not knowable without simulating it), so the "misuse" boundary is fuzzier.
- **Impact:** The spending limit does not bound the most general value-moving transaction type on the chain; a
  user's recorded spend history is also silently wrong (app-call spends are recorded as $0 or not at all).
- **Affected component:** `BiatecOIDC/Helper/AlgorandTransactionInspector.cs:122-127`.
- **Current mitigations:** None. The `Other` kind is documented as "not subject to spending-limit checks", so the
  behavior is intentional, but it is not surfaced to the user.
- **Recommended further mitigation:** In increasing order of effort: (a) mark `Kind == Other` transactions as
  "unpriced" in the sign response and the spend ledger, so the user's history is not silently misleading;
  (b) require a distinct claim/scope for `appl` transactions so a user can grant "payments only"; (c) use algod's
  `/v2/transactions/simulate` to obtain the inner-transaction set and price that — the correct long-term answer
  and a substantial piece of work.
- **Status:** Open.
- **History:**
  - 2026-08-04 — claude-code-ai-review-4: opened at 40%, corresponds to finding M-03 in
    [audit-report-2026-04-08-34459ac-claude-code-ai-review-4.md](audit-report-2026-04-08-34459ac-claude-code-ai-review-4.md).

### R-029 — Identity-only token can force unbounded writes to the user's cloud storage; activation registry has no concurrency control

- **Description:** Two related defects on `GET /wallet/address/{seedAddress}/{slot}`. **(a)** The endpoint is
  gated as read-only (`TryAuthenticate(requiredClaim: null, …)` — any validly-authenticated caller, no `sign`,
  no `manage-limits`) yet performs up to four sequential load-decrypt-modify-encrypt-upload cycles against the
  user's own Drive/OneDrive, one per chain family. `slot` is an unconstrained caller-supplied `int`, and each
  distinct slot adds four registry entries, so a client holding nothing but an `openid` token can grow
  `AddressActivations.%AESID%.dat` without bound. Because `ResolveSignerAsync` decrypts that whole file on every
  sign/limits/info call, an inflated registry degrades and eventually breaks every wallet operation for that
  user — a denial of service against the user's own wallet, from the lowest-privilege token the system issues,
  with no rate limiting anywhere in the path. **(b)** `AddressActivationService.ActivateAsync` is a bare
  read-modify-write with no equivalent of the `SaveVaultWithConcurrencyCheckAsync` mitigation added for R-021, so
  concurrent activations — including the four issued back-to-back by a single `GetAddress` call — can silently
  lose an entry, after which the affected address stops resolving until re-derived.
- **Likelihood (5-year misuse probability):** 15% — reasoning: availability-only, self-scoped (the victim is the
  account whose own client is misbehaving), no fund loss, and it requires a hostile or buggy client the user has
  already authorized. The concurrency half (b) is more likely to occur *accidentally* than to be exploited
  deliberately, and its consequence is a recoverable re-activation rather than harm.
- **Impact:** (a) Loss of availability of the wallet API for a targeted user, plus consumption of that user's own
  cloud storage quota and possible provider-side throttling. (b) Silent loss of activation entries, causing
  signing to fail for an externally-rekeyed address until it is activated again.
- **Affected component:** `BiatecOIDC/Controllers/WalletController.cs:606-677`;
  `BiatecOIDC/BusinessLogic/AddressActivationService.cs:53-77`.
- **Current mitigations:** None. R-021's concurrency fix covers the seed vault only and does not extend to this
  file.
- **Recommended further mitigation:** Bound `slot` to a sane range (the ARC-76 use case does not need 2³¹ slots);
  cap the registry's entry count; add per-user rate limiting on the derive endpoint; consider requiring a
  write-capable claim for an endpoint that writes. Apply the same baseline-bytes concurrency check
  `SaveVaultWithConcurrencyCheckAsync` uses, or batch one `GetAddress` call's four activations into a single
  load/save.
- **Status:** Open.
- **History:**
  - 2026-08-04 — claude-code-ai-review-4: opened at 15%, corresponds to finding M-04 in
    [audit-report-2026-04-08-34459ac-claude-code-ai-review-4.md](audit-report-2026-04-08-34459ac-claude-code-ai-review-4.md).
    Opened as a new entry rather than by re-opening R-021, even though (b) is the same defect class in a
    different file — following the precedent the second audit set for R-011/R-018.

### R-030 — A configuration-gated authentication bypass ships in the production artifact (mock identity/storage provider)

- **Description:** `JwtIssuerController.MockSignIn` is `[AllowAnonymous]`, takes no credential, and signs the
  browser into a full cookie session as a configured synthetic identity, then hands off to the real
  `AuthorizeCallback` — so the resulting OIDC code and access token are indistinguishable from a genuine
  sign-in. `MockCloudStorageProvider.IsConfigured` returns `true` unconditionally, and the corresponding seed
  vaults are created from **mnemonics stored in plaintext configuration** (`CloudServices:Mock:Accounts[].Mnemonic`).
  The only gate is configuration: the provider is registered solely when `CloudServices:Mock:Enabled` is true and
  at least one account is configured, and the picker hides the button unless `/authorize` named a configured
  `scopeId`. That gating is well built, and this audit verified no committed configuration enables it
  (`appsettings.json` ships `"Enabled": false` with an empty list; no `k8s/main/*` or `k8s/stage/*` manifest
  mentions `Mock`). The residual risk is the shape itself: a complete authentication bypass exists in the shipped
  production artifact, one environment variable away from being live, with total blast radius over the configured
  identities. **Threat actor:** an operator misconfiguring stage/production, an attacker with ConfigMap/Secret
  write access, or an engineer enabling it to debug an incident and forgetting to remove it.
- **Likelihood (5-year misuse probability):** 8% — reasoning: correctly gated today, not enabled anywhere
  committed, blast radius limited to synthetic test identities rather than real users' vaults, and enabling it
  requires an action (a config change) that is itself visible in review. Non-zero because "debug/test bypass
  accidentally enabled in production" has a poor industry track record over multi-year horizons, because stage
  and production share a deployment pipeline and manifest structure, and because the same repository already
  found it necessary to add a startup fail-fast for exactly the analogous "dangerous placeholder value left
  configured" scenario (R-019/R-023).
- **Impact:** If ever enabled in a production or stage deployment, anyone who can reach `/authorize` obtains full
  wallet access — including `sign`-scoped tokens — to the configured mock identities' vaults. Does not grant
  access to real users' vaults, since those are bound to real provider identities and separate storage.
- **Affected component:** `BiatecOIDC/Controllers/JwtIssuerController.cs:804-836` (and the picker/fast-track paths
  at :260-296, :695-711, :738-790); `BiatecSelfCustodyCore/Providers/MockCloudStorageProvider.cs`;
  `BiatecSelfCustodyCore/Providers/MockCloudStorage.cs`; `BiatecOIDC/Program.cs:96-102,155-159,397-421`;
  `BiatecOIDC/MOCK_TESTING.md`.
- **Current mitigations:** Disabled by default and not enabled in any committed configuration; requires both an
  `Enabled` flag and at least one configured account before the provider is registered at all; hidden from the
  default provider picker unless the authorize request explicitly named a configured mock `scopeId`; documented
  as internal-only and deliberately not linked from public integration docs.
- **Recommended further mitigation:** Apply the R-019/R-023 precedent — fail fast at startup if
  `CloudServices:Mock:Enabled` is true while `IHostEnvironment` is not `Development`, making the bypass
  structurally unreachable in production rather than merely off by default. Alternatively compile the mock
  provider and its controller actions out of Release builds.
- **Status:** Open.
- **History:**
  - 2026-08-04 — claude-code-ai-review-4: opened at 8%, corresponds to finding L-01 in
    [audit-report-2026-04-08-34459ac-claude-code-ai-review-4.md](audit-report-2026-04-08-34459ac-claude-code-ai-review-4.md).
    The mock provider is new since the third audit; this is the first audit to review it.

### R-013 — CI/CD pipeline has no in-workflow deployment gate; `k8s/main/conf` contents unverified

- **Description:** `build-api.yml` deploys directly to production on every push to `master` with no `environment:`
  protection block visible in the workflow file itself; `k8s/main/conf/*` was not inspected for accidentally
  plaintext secret material in this audit.
- **Likelihood (5-year misuse probability):** 5% (revised 2026-08-01 from 10%) — reasoning: the pipeline no longer
  deploys directly to production on every push to `master` (see Current mitigations and History) — the specific
  mechanism this entry's Description names is gone. Residual likelihood reflects that GitHub branch-protection
  configuration on `master` remains unverifiable from repository content alone, and that a manual production
  promotion is still a human action that could itself be careless or (if the promoting account were compromised)
  malicious, though this is a materially smaller and better-gated attack surface than "any push reaches
  production automatically."
- **Impact:** Unchanged — a compromised or careless production promotion would still deploy directly to
  production; if `k8s/main/conf-*` contains secret material, it would be exposed at a lower protection tier than
  intended (see R-019/P-01's `AesOptions`/`ProviderTokenProtection` findings, which are the concrete realization
  of this).
- **Affected component:** `.github/workflows/deploy-stage.yml`, `.github/workflows/promote-production.yml`
  (formerly `build-api.yml`); `k8s/main/conf-mcp/*`, `k8s/main/conf-oidc/*` (formerly `k8s/main/conf/*`).
- **Current mitigations:** `docs/KUBE_CONFIG_SECURITY.md`-described namespace-scoped, time-limited kubeconfig
  limits blast radius of the CI credential itself even if a workflow run is triggered maliciously. **New as of
  2026-08-01:** `deploy-stage.yml` now deploys only to the `stage` environment on every push to `master`;
  `promote-production.yml` is `workflow_dispatch`-only (a human must manually trigger it with a specific,
  already-stage-deployed image tag) and never rebuilds anything — see `docs/STAGE_ENVIRONMENT.md`. This directly
  closes the "every push to master deploys straight to production with no gate" mechanism the entry was originally
  opened to describe.
- **Recommended further mitigation:** Verify/require GitHub branch-protection on `master`; verify who has
  permission to trigger `promote-production.yml` (a `workflow_dispatch` workflow is only as safe as who can invoke
  it). **Not a code change** for the branch-protection half — requires manually checking GitHub repository
  settings, which this audit cannot perform.
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
  - 2026-01-08 — claude-code-ai-review-3: likelihood revised from 10% to 5%, corresponds to finding P-04 in
    [audit-report-2026-01-08-69d410c-claude-code-ai-review-3.md](audit-report-2026-01-08-69d410c-claude-code-ai-review-3.md).
    The single-workflow, push-to-master-deploys-to-production pipeline this entry described no longer exists —
    replaced by a stage-only auto-deploy plus a manually-gated production promotion, an unprompted architectural
    improvement made independently of any audit recommendation. Not closed outright since GitHub
    branch-protection settings remain unverified and a manual promotion step is still a human trust boundary.
  - 2026-08-04 — claude-code-ai-review-4: likelihood left unchanged at 5%. Neither workflow file changed in the
    `69d410c..34459ac` range, and GitHub branch-protection settings and `promote-production.yml`'s trigger
    permissions remain unverifiable from repository content alone — the same standing manual follow-up this
    entry has carried since the first audit. `k8s/main/conf-*` was re-inspected this pass and its remaining
    committed key material is now unmistakable all-zero placeholder text with a startup fail-fast behind it
    (see R-019/R-023), so the "contents unverified" half of this entry's original concern is materially
    narrower than when it was opened.

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
- **Status:** Closed (mitigated).
- **History:**
  - 2026-24-07 — claude-code-ai-review-2: opened at 5%, corresponds to finding G-01 in
    [audit-report-2026-24-07-173793a-claude-code-ai-review-2.md](audit-report-2026-24-07-173793a-claude-code-ai-review-2.md).
    Discovered while independently re-verifying R-011's closure — the fix was found genuine for R-011's own
    originally-cited scope but was not extended to this file.
  - 2026-01-08 — claude-code-ai-review-3: re-confirmed unchanged after the project restructure. The same three
    tools' generic catch blocks at their new location (`BiatecMCP/MCP/BiatecMCPGoogle.cs`) still return raw
    `ex.Message`/`ex.Result.Message`. Status/likelihood unchanged.
  - 2026-08-02 — engineering-remediation: closed. `BiatecMCPGoogle`'s three generic `catch (Exception ex)` blocks
    (`GetAccountAddress`, `TransferAsset`, `OptIn`) now call a new `SanitizeForToolResponse(ex, toolName)` helper
    that logs the full exception via a newly-injected `ILogger<BiatecMCPGoogle>` and returns a generic,
    non-identifying message to the MCP tool response — the same pattern R-011's fix already applied to
    `GoogleDriveRepository`/`DriveController`/`DevicePairingController`. `Algorand.ApiException<...>.Result.Message`
    passthroughs (legitimate, already user-facing Algorand node error text, e.g. "insufficient balance") were
    deliberately left untouched, per this entry's own recommended remediation's distinction. No test currently
    asserts on this behavior (no test previously asserted on the raw message content either, so no regression
    risk) — a future audit should confirm this by triggering an unexpected exception in each of the three tools
    and checking the response no longer contains `.NET`-internal exception text.
  - 2026-08-04 — claude-code-ai-review-4: closure confirmed against the current, substantially larger tool
    surface (20 tools in `BiatecMCP/MCP/BiatecMCP.cs`, up from the three this entry was opened against). Every
    generic `catch (Exception ex)` block routes through `SanitizeForToolResponse(ex, nameof(...))`; the
    remaining direct uses of `ex.Message` are confined to typed, caller-facing exceptions
    (`WalletApiException`, argument/format validation) whose messages are authored text, not internal detail.
    Status unchanged (Closed). The suggested trigger-an-unexpected-exception verification was still done by
    reading, not by execution.

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
- **Status:** Mitigated.
- **History:**
  - 2026-24-07 — claude-code-ai-review-2: opened at 20%, corresponds to finding G-02 in
    [audit-report-2026-24-07-173793a-claude-code-ai-review-2.md](audit-report-2026-24-07-173793a-claude-code-ai-review-2.md).
    This is the first audit to inspect `k8s/main/conf/*` contents (the first audit, F-13, explicitly deferred this
    check; the 2026-07-24 remediation pass also did not perform it, per R-013's own history). Opened as a new,
    more specific entry rather than folded into R-013, since R-013's original concern was general ("contents
    unverified") while this entry describes a specific, reproducible piece of evidence (an unchanged-for-a-year,
    syntactically-real-looking committed key) with its own severity and remediation path.
  - 2026-01-08 — claude-code-ai-review-3: re-confirmed unchanged after the project restructure — the exact same
    `Key`/`IV` bytes are present, byte-for-byte, in both `k8s/main/conf-mcp/appsettings.json` (formerly
    `k8s/main/conf/appsettings.json`) and the new `k8s/main/conf-oidc/appsettings.json`, confirmed via
    `git log --follow -p`. Partial progress noted: the surrounding fields were relabeled `"ActiveKeyId":
    "placeholder"`/`"KeyId": "placeholder"` as an incidental part of the unrelated AES key-ring-rotation feature,
    which satisfies part of this entry's recommendation #2 (an unmistakable placeholder label) but not the
    substance of it (the `Key`/`IV` bytes themselves are unchanged and still real-looking) nor recommendation #3
    (no startup fail-fast check against the known placeholder value was added — `AesKeyRingResolver.GetActiveKey`
    validates only syntactic well-formedness, not whether the resolved value matches this specific known-committed
    sentinel). Likelihood left unchanged at 20% — no new evidence bears on whether the committed value is actually
    overridden in production. **Scope note:** this audit found the identical pattern now also applies to a second,
    newly-introduced key ring, `ProviderTokenProtection` (`k8s/main/conf-oidc/appsettings.json`), which protects
    every relying party's cached Google/Microsoft provider access *and refresh* tokens — arguably higher-impact
    than this entry's original `AesOptions` scope, since a live refresh token grants ongoing read/write access to
    the user's cloud storage, not just decryption of one already-encrypted vault file. Tracked as a new, paired
    entry, **R-023**, rather than folded into this one, so each key ring's status can be tracked and closed
    independently (they are deployed via the same `google-account-main-app-secret` mechanism but are logically
    separate secrets with separate blast radii).
  - 2026-08-02 — engineering-remediation: revised to Mitigated (from Open). Two of this entry's three
    recommendations are now implemented: (2) the committed `Key`/`IV` bytes in `k8s/main/conf-mcp/appsettings.json`
    and `k8s/main/conf-oidc/appsettings.json` were replaced with an unmistakable all-zero placeholder
    (`AAAA...=`/`AAAA...==`) — no longer visually indistinguishable from real key material; (3) a new
    `AesKeyRingResolver.EnsureActiveKeyIsNotKnownPlaceholder` check, called from `CloudAccountRepository`'s and
    `ProviderAccessTokenProtector`'s constructors alongside their existing `GetActiveKey` fail-fast (both still
    gated to `!environment.IsDevelopment()`, since this repository's own root `appsettings.json` files
    deliberately reuse the same example key for local development), now throws `InvalidOperationException` at
    startup if the resolved active key matches either the new all-zero sentinel or the original real-looking
    value this entry originally flagged — so a deployment whose secret override is missing can no longer start up
    successfully and silently serve traffic under a known-bad key. Recommendation (1) — out-of-band confirmation
    that the live production `Secret` actually overrides this value — remains outside what a code change can
    verify; this is why the entry is revised to **Mitigated** rather than **Closed**, and likelihood is left at
    20% pending that out-of-band confirmation (the fail-fast fix reduces the *consequence* of a missing override
    from "silent" to "loud crash-on-startup," which is a meaningful improvement, but does not by itself confirm
    whether an override is currently configured). Recommendation (4), precautionary rotation, was not performed
    by this remediation pass. All 4 committed k8s config files (`k8s/main/conf-mcp`, `k8s/main/conf-oidc`,
    `k8s/stage/conf-mcp-stage`, `k8s/stage/conf-oidc-stage`) were updated identically. A future audit should
    verify: the fail-fast actually fires against the new placeholder in a non-Development environment (e.g. an
    integration test instantiating `CloudAccountRepository`/`ProviderAccessTokenProtector` under a
    `Production`/`Staging` `IHostEnvironment` with the placeholder configured — no such test was added by this
    pass), and should still pursue the outstanding out-of-band verification against the live `Secret`.
  - 2026-08-04 — claude-code-ai-review-4: mitigation independently re-verified in code and configuration, and
    **confirmed genuine**. `k8s/main/conf-oidc/appsettings.json` and `k8s/main/conf-mcp/appsettings.json` now
    carry only the unmistakable all-zero placeholder, and `AesKeyRingResolver.EnsureActiveKeyIsNotKnownPlaceholder`
    rejects both that value and the two historical real-looking ones. Confirmed the check is invoked for **both**
    key rings — `CloudAccountRepository.cs:56` (`AesOptions`) and `ProviderAccessTokenProtector.cs:53`
    (`ProviderTokenProtection`, see R-023) — and only inside the `!environment.IsDevelopment()` guard, so local
    development is unaffected. Status and likelihood left unchanged at Mitigated/20%: recommendation (1),
    out-of-band confirmation of whether the previously-committed value was ever live in production, remains
    outstanding and is still the only thing standing between this entry and closure. The non-Development
    fail-fast integration test the previous history line recommended still does not exist.

### R-020 — Vault-backup OAuth flow has no CSRF binding to the browser session completing consent

- **Description:** The cross-cloud vault-backup feature's OAuth round trip to the *target* cloud provider
  (`VaultBackupController.Authorize`/`Callback`, `VaultBackupService.HandleCallbackAsync`) uses its unguessable
  `linkId` as the OAuth `state` parameter, but never binds that value to the browser session that actually
  completes the consent screen — only to whichever Biatec account called `POST /wallet/backup/start`. An attacker
  who holds any valid `sign`-scoped Biatec token for their own account can generate a `linkId`, send an unrelated
  victim a link to `GET /wallet/backup/authorize?linkId=...`, and — if the victim merely completes the resulting,
  entirely ordinary-looking consent screen while logged into their own Google/Microsoft account — cause the
  victim's captured provider access token to be spent (via the attacker's own subsequent
  `POST /wallet/backup/complete` call) to upload the **attacker's** encrypted vault into the **victim's** cloud
  storage, under the exact file name the victim's own vault uses. On Microsoft/OneDrive this is a direct,
  in-place overwrite (HTTP `PUT` to a fixed item path); on Google Drive it creates an ambiguous same-named
  duplicate that can corrupt which file a future read resolves to.
- **Affected component:** `BiatecOIDC/Controllers/VaultBackupController.cs` (`Authorize`, `Callback`);
  `BiatecOIDC/BusinessLogic/VaultBackupService.cs` (`StartAsync`, `HandleCallbackAsync`, `CompleteAsync`);
  `BiatecSelfCustodyCore/Providers/MicrosoftCloudStorageProvider.cs` (`UploadAsync`);
  `BiatecSelfCustodyCore/Providers/GoogleCloudStorageProvider.cs` (`UploadAsync`).
- **Likelihood (5-year misuse probability):** 15% — reasoning: exploitation requires no privileged access and no
  prior relationship between attacker and victim beyond the victim clicking one link and completing one consent
  screen that looks entirely legitimate (a real Biatec-branded OAuth consent prompt for a real permission, just for
  the wrong "whose backup" party) — a realistic bar for a targeted or semi-automated phishing campaign. Likelihood
  is not rated higher because it does require some social-engineering step (it is not a pure server-side
  vulnerability exploitable with no victim interaction at all), and this system's current user base/exposure is
  not yet established at scale. Likelihood is not rated lower because self-custody cryptocurrency users are a
  demonstrated, high-value, recurring phishing target industry-wide, making the social-engineering precondition
  more realistic here than in an average web application.
- **Impact:** High. Integrity/availability, not confidentiality or theft — the attacker never obtains the victim's
  key material and the captured victim token is spent exactly once, for exactly this write, never cached or reused.
  But the practical consequence is a remote, unauthenticated (from the victim's perspective) denial-of-service
  against a specific victim's self-custody vault: on OneDrive, the victim's real encrypted vault file is silently
  destroyed and replaced with undecryptable attacker ciphertext, surfaced to the victim only as a generic
  "Unable to load the account" error with no indication of cause.
- **Current mitigations:** The `linkId` itself is unguessable (cryptographically random) and single-use (atomic
  Redis `GETDEL` on completion), which prevents an attacker from *replaying* someone else's legitimate backup link
  — but does nothing to prevent an attacker from using their *own*, legitimately-obtained link against an
  unrelated victim, which is the actual mechanism of this finding.
- **Recommended further mitigation:** Bind `Authorize`/`Callback` to the completing browser via a purpose-built
  anti-CSRF mechanism (e.g. an `HttpOnly`/`SameSite=Lax` cookie set by `Authorize` and checked in `Callback`),
  independent of the OAuth `state` value, which currently only identifies *which pending backup* this is, not
  *who is authorized to complete it*. See the full remediation options (including a defense-in-depth file-naming
  change) in the audit report's H-01 finding. **Superseded by the 2026-08-02 remediation below** — a same-browser
  cookie alone would not actually have closed this gap (see that entry for why) and was not the fix implemented.
- **Status:** Mitigated.
- **History:**
  - 2026-01-08 — claude-code-ai-review-3: opened at 15%, corresponds to finding H-01 in
    [audit-report-2026-01-08-69d410c-claude-code-ai-review-3.md](audit-report-2026-01-08-69d410c-claude-code-ai-review-3.md).
    The vault-backup feature is new since the second audit; this is the first audit to review it.
  - 2026-08-02 — engineering-remediation: revised to Mitigated. On reflection, a plain anti-CSRF cookie (this
    entry's originally-recommended fix) would **not** actually have closed this gap: the victim's browser
    genuinely is the same browser completing both `Authorize` and `Callback` in the attack scenario (it is a
    normal, single-browser redirect chain the victim initiates by clicking one link), so "same browser" is not
    the property that was missing — "the right account" is. Instead, `VaultBackupController.Authorize` and
    `Callback` now both call a new `EnsureBrowserOwnsBackup` check that requires the browser's *ambient Biatec
    cookie session* (the same cookie-authentication infrastructure `JwtIssuerController`'s `/authorize` already
    relies on for `User.Identity.IsAuthenticated`) to be signed in with an email matching the pending backup's
    own `Email` — refusing with a plain-language error page otherwise, before ever redirecting to the target
    provider's consent screen. An unrelated victim, who by construction of this attack has never signed in to
    Biatec as the attacker's account, has no ambient session satisfying this check and is refused immediately.
    `Callback` re-checks the same condition as defense in depth, in case the ambient session changed between the
    two browser round trips. Revised to **Mitigated** rather than **Closed** because this fix depends on the
    completing browser actually holding a live Biatec cookie session for the correct account at the time it
    visits `Authorize` (e.g., from having recently completed the RP's own `/authorize` sign-in in the same
    browser) — a legitimate user whose Biatec cookie has since expired would now see a "must be signed in" error
    instead of completing their own legitimate backup, which is a UX/product question (should `Authorize`
    instead redirect through a fresh Biatec sign-in first?) not resolved by this pass. No automated test was
    added for `EnsureBrowserOwnsBackup` (no existing `VaultBackupControllerTests.cs` test file exists at all —
    only `VaultBackupServiceTests.cs`, which this change does not touch) — a future audit should add a
    controller-level test asserting a mismatched/absent ambient session is refused before any redirect to the
    target provider, and should re-attempt the exact H-01 reproduction steps from the third audit's report to
    confirm the attack no longer succeeds.
  - 2026-08-04 — claude-code-ai-review-4: fix re-verified in code and **confirmed genuine** — both `Authorize`
    and `Callback` call `EnsureBrowserOwnsBackup`, which requires the ambient cookie session to belong to the
    same account as the pending backup, which is indeed the property this attack needed and which a same-browser
    anti-CSRF token would not have provided. Status and likelihood left unchanged at Mitigated/15%: the
    verification was static only (the third audit's reproduction steps were not re-attempted dynamically — no
    running deployment was in scope, see this audit's §1.2), and the recommended `VaultBackupControllerTests.cs`
    still does not exist.

### R-021 — No optimistic-concurrency control on seed-vault writes; concurrent mutations can silently lose a seed creation or a primary-seed switch

- **Description:** `CreateSeedAsync`, `SwitchPrimarySeedAsync`, and the legacy-vault migration path all follow an
  unguarded read-modify-write pattern against the seed-vault file, with no ETag/If-Match precondition, compare-
  and-swap, or distributed lock. Two concurrent mutations against the same account (a double-submitted retry, or
  two legitimate operations issued moments apart) can both read the same starting state; whichever write lands
  last silently wins, discarding the other's change with no error surfaced to either caller. The specific
  sequences this can lose (a just-created seed never persisting, or a primary-seed switch silently not taking
  effect) are exactly the operations the multi-seed/on-chain-rekey feature exists to make safe.
- **Affected component:** `BiatecSelfCustodyCore/Repository/CloudAccountRepository.cs` (`CreateSeedAsync`,
  `SwitchPrimarySeedAsync`); `BiatecSelfCustodyCore/Helper/EncryptedKeyRingFileStore.cs` (`SaveAsync`);
  both `ICloudStorageProvider.UploadAsync` implementations (neither provider-side call uses a conditional write).
- **Likelihood (5-year misuse probability):** 8% — reasoning: this requires a race condition (concurrent requests
  against the same account within a narrow window), not an attacker with no other access — realistic triggers are
  client-side retry logic double-submitting a request, or a legitimate user issuing a create-then-switch sequence
  in quick succession from an impatient UI, rather than a deliberate attack. Rated non-negligible because the
  multi-seed/rekey feature is specifically the recovery-from-compromise path, where a silently-lost state change
  at exactly the wrong moment (e.g., believing a primary-seed switch after an on-chain rekey succeeded when it
  didn't) has outsized consequence relative to how rare the triggering race needs to be.
- **Impact:** Medium. No seed's mnemonic is ever exposed and no existing seed is ever deleted by this race — only
  the losing write's *addition/change* is discarded. Consequence is a caller believing a state change (a new
  recovery seed exists; primary has switched) that didn't actually persist, which for the primary-switch case
  specifically mirrors the exact failure mode `CLAUDE.md` documents as unsafe ("switching primary before
  \[on-chain rekey confirmation\] would make Biatec sign with a key the account no longer recognizes") — just
  triggered by a race instead of caller error.
- **Current mitigations:** None specific to this path. `SwitchPrimarySeedAsync` requiring the `sign` claim, and
  `CreateSeedAsync` requiring the stricter `rekey` claim, limit who can trigger either mutation at all, but do not
  address the race between two authorized mutations.
- **Recommended further mitigation:** Add optimistic-concurrency detection to the vault read-modify-write cycle
  (e.g. Microsoft Graph's native `If-Match`/`eTag` support on `PUT`; a read-current-hash-and-compare pattern for
  Google Drive) so a changed-since-read file fails the mutation with a caller-visible, retryable error instead of
  silently being overwritten.
- **Status:** Mitigated.
- **History:**
  - 2026-01-08 — claude-code-ai-review-3: opened at 8%, corresponds to finding M-01 in
    [audit-report-2026-01-08-69d410c-claude-code-ai-review-3.md](audit-report-2026-01-08-69d410c-claude-code-ai-review-3.md).
    The multi-seed vault (and its read-modify-write pattern) is new since the second audit; this is the first
    audit to review it.
  - 2026-08-02 — engineering-remediation: revised to Mitigated. `CreateSeedAsync`, `SwitchPrimarySeedAsync`, and
    the first-seed auto-creation branch of `LoadVaultEnsuringAtLeastOneSeedAsync` now each capture the active
    file's raw (still-encrypted) bytes immediately after their own read, and re-download and compare those bytes
    immediately before their write, via a new `CloudAccountRepository.SaveVaultWithConcurrencyCheckAsync`
    helper; a mismatch throws a new `VaultConcurrencyConflictException` instead of silently overwriting, which
    `WalletController.CreateSeed`/`SwitchPrimarySeed` now catch and surface as `409 Conflict` (`vault_concurrency_
    conflict`) so the caller can retry against current state. This is a **best-effort check-then-act
    re-verification, not a provider-enforced atomic compare-and-swap** (neither `GoogleCloudStorageProvider` nor
    `MicrosoftCloudStorageProvider` was changed to use a native conditional write, e.g. Graph's `If-Match`/`eTag`)
    — it narrows the race window from "the entire request" down to "the gap between the re-check and the
    upload that immediately follows it," which is why this is revised to **Mitigated** rather than **Closed**.
    The legacy-format migration write inside `LoadVaultOrEmptyAsync` was deliberately left unprotected (out of
    scope for this pass) — it is a one-time, largely idempotent migration (a race there produces two
    functionally-equivalent single-seed vaults from the same original mnemonic, not the seed-loss/lost-switch
    failure mode this entry describes), unlike the three protected call sites. All 107 `BiatecMCPTests` still
    pass with this change (the extra `TryDownloadAsync` round trips did not break any existing test double's
    expectations), but no new test specifically exercises the conflict path itself (i.e., asserting that a
    changed-underlying-file causes `VaultConcurrencyConflictException`/409) — a future audit should add one and
    should evaluate whether a native provider-side conditional write is worth the added complexity given the
    residual TOCTOU window this fix does not eliminate.
  - 2026-08-04 — claude-code-ai-review-4: fix re-verified against the code (not against the remediation pass's
    own description of itself) and **confirmed genuine** for the seed vault —
    `SaveVaultWithConcurrencyCheckAsync` is present and used by `CreateSeedAsync`, `SwitchPrimarySeedAsync`,
    `SeedTestVaultAsync`, and the first-seed branch of `LoadVaultEnsuringAtLeastOneSeedAsync`, surfacing
    `409 vault_concurrency_conflict`. Status and likelihood left unchanged (still Mitigated, not Closed, for the
    best-effort/TOCTOU reason already recorded). **However**, the identical defect exists unfixed in
    `AddressActivationService.ActivateAsync` — a file added after this fix was written and not covered by it —
    tracked as **R-029** rather than by re-opening this entry, following the R-011/R-018 precedent. The
    conflict-path regression test the previous history line recommended still does not exist.

### R-022 — Known moderate-severity advisory (GHSA-59j7-ghrg-fj52) in pinned `Microsoft.IdentityModel.*`/`System.IdentityModel.Tokens.Jwt` 5.5.0 packages

- **Description:** The package family used throughout `JwtIssuerService.cs` to sign/validate every access, ID,
  and refresh token this system issues, and by both apps' Google/Microsoft `AddOpenIdConnect` handlers to validate
  the identity provider's own tokens, is pinned at version 5.5.0, which NuGet's own offline advisory database
  (surfaced as `NU1902` build warnings) flags against `GHSA-59j7-ghrg-fj52`, rated moderate severity. This is not a
  newly-introduced defect — the same version was already pinned at the time of the first audit — but no prior
  audit's methodology included a dependency-vulnerability check, so this is the first audit to surface it.
- **Affected component:** `BiatecMCP.csproj`, `BiatecSelfCustodyCore.csproj` (transitively `BiatecOIDC` too, via
  its project reference), and both test projects.
- **Likelihood (5-year misuse probability):** 10% — reasoning: this audit did not fetch the advisory's full
  technical detail (no network access used for this engagement), so exploitability against this codebase's
  specific usage pattern is unconfirmed either way; the likelihood is a generic estimate for a moderate-severity,
  publicly-known advisory in an actively-maintained package family with security researcher attention, weighted
  down because this codebase does not appear to expose any obviously-attacker-controlled input directly into the
  vulnerable library surface beyond what any OIDC token-validation code path inherently does.
- **Impact:** Medium-to-High if the advisory's specific mechanism turns out to be reachable — this package family
  sits directly on the boundary between "a token is cryptographically valid" and "a token is accepted," the most
  security-sensitive function in `BiatecOIDC`.
- **Current mitigations:** None specific; general defense-in-depth (audience/issuer/lifetime validation logic in
  `JwtIssuerService`, R-003/R-014's fixes) is independent of this package-level concern and would not necessarily
  mitigate a vulnerability inside the package itself.
- **Recommended further mitigation:** Upgrade to a patched version of `System.IdentityModel.Tokens.Jwt`/
  `Microsoft.IdentityModel.JsonWebTokens`/`Microsoft.IdentityModel.Tokens`, re-run the full test suite, and add a
  recurring dependency-vulnerability scan (e.g. `dotnet list package --vulnerable` in CI) so future advisories are
  caught automatically rather than depending on an auditor noticing build output.
- **Status:** Closed (mitigated).
- **History:**
  - 2026-01-08 — claude-code-ai-review-3: opened at 10%, corresponds to finding M-02 in
    [audit-report-2026-01-08-69d410c-claude-code-ai-review-3.md](audit-report-2026-01-08-69d410c-claude-code-ai-review-3.md).
  - 2026-08-02 — engineering-remediation: closed. `BiatecSelfCustodyCore.csproj` now explicitly pins
    `Microsoft.IdentityModel.JsonWebTokens`, `Microsoft.IdentityModel.Protocols.OpenIdConnect`,
    `Microsoft.IdentityModel.Tokens`, and `System.IdentityModel.Tokens.Jwt` to `8.21.0` — the same version
    `BiatecOIDC.csproj` already referenced directly — overriding the vulnerable `5.5.0` transitively pulled in
    by `Google.Apis.Auth`/`Google.Apis.Auth.AspNetCore3` 1.75.0. Confirmed via `dotnet build Biatec.slnx`: the
    `NU1902` advisory warnings for `GHSA-59j7-ghrg-fj52` are no longer emitted anywhere in the solution, and all
    377 existing tests (107 + 270) still pass, evidencing no breaking API changes from the version bump.
  - 2026-08-04 — claude-code-ai-review-4: closure independently confirmed. All four packages are pinned at
    `8.21.0` across `BiatecOIDC.csproj`/`BiatecSelfCustodyCore.csproj`, and
    `dotnet list Biatec.slnx package --vulnerable --include-transitive` reports **no** vulnerable packages,
    direct or transitive — the first time this registry records an actual vulnerability-scanner result rather
    than the absence of `NU1902` build warnings. Status unchanged (Closed). The recurring-CI-scan half of this
    entry's recommended mitigation is still not implemented: there is no automated dependency-scan job in either
    workflow, so the next advisory will again depend on an auditor noticing.

### R-023 — Committed `ProviderTokenProtection` key/IV of unconfirmed live status (paired with R-019)

- **Description:** `k8s/main/conf-oidc/appsettings.json`'s `ProviderTokenProtection` key ring (introduced by the
  provider-access-token-caching feature, new since the second audit) has the identical shape and identical
  unresolved question as R-019's `AesOptions` finding: a syntactically valid, correctly-sized AES-256 key/IV,
  labeled with a `KeyId`/`ActiveKeyId` of the literal string `"placeholder"`, deployed via the same
  `google-account-main-app-secret` `envFrom` mechanism, with no way for this audit to confirm from repository
  content alone whether it is actually overridden in production. This key ring protects the `provider_token`/
  `provider_refresh_token` claims embedded in every access/refresh token `BiatecOIDC` issues — decrypting a
  captured token's refresh-token claim under a live committed key would hand an attacker an ongoing, renewable
  Google/Microsoft credential for that user's cloud storage, not merely offline access to one already-encrypted
  vault file.
- **Affected component:** `k8s/main/conf-oidc/appsettings.json` (`ProviderTokenProtection` section);
  `BiatecOIDC/BusinessLogic/ProviderAccessTokenProtector.cs`; `k8s/main/deployment-oidc.yaml`.
- **Likelihood (5-year misuse probability):** 20% — reasoning: identical to R-019's, since the underlying
  uncertainty (is the committed value ever live?) and mitigating factors (a plausible, standard, code-consistent
  design under which it is inert placeholder text) are the same. See R-019's likelihood reasoning for the full
  discussion; not reduced further here because, unlike R-019's `AesOptions`, this key ring has no prior audit
  history establishing any additional context.
- **Impact:** Critical if the committed value is ever live — arguably exceeding R-019's impact, since a live
  provider refresh token grants ongoing read/write/delete access to a user's cloud storage for as long as the
  token remains valid, not just offline decryption of one already-encrypted file.
- **Current mitigations:** Same as R-019 — the Kubernetes `Secret`+`envFrom` mechanism, if actually configured to
  override `ProviderTokenProtection__Keys__0__Key`/`__IV`, would fully mitigate via standard ASP.NET Core
  configuration precedence.
- **Recommended further mitigation:** Same remediation priority order as R-019, applied to this key ring in
  parallel (both are deployed via the same secret, so verification/rotation work should cover both together): (1)
  out-of-band verification against the live `Secret` — the only item still outstanding, see History; (2)/(3) an
  unmistakable placeholder and a startup fail-fast check against it are now implemented for both key rings; (4)
  precautionary rotation if (1) cannot rule out the committed value having been live.
- **Status:** Mitigated.
- **History:**
  - 2026-01-08 — claude-code-ai-review-3: opened at 20%, corresponds to finding P-01 in
    [audit-report-2026-01-08-69d410c-claude-code-ai-review-3.md](audit-report-2026-01-08-69d410c-claude-code-ai-review-3.md).
    The `ProviderTokenProtection` key ring is new since the second audit; this is the first audit to review it.
    Opened as a paired entry alongside R-019 rather than folded into it, so each key ring's remediation can be
    tracked and closed independently even though they share the same deployment mechanism and remediation shape.
  - 2026-08-02 — engineering-remediation: revised to Mitigated (from Open), in lockstep with R-019's identical
    revision (both key rings share the same fix, applied in the same commit series). The committed `Key`/`IV` in
    `k8s/main/conf-oidc/appsettings.json`'s `ProviderTokenProtection` section (and the equivalent stage file,
    `k8s/stage/conf-oidc-stage/appsettings.json`) were replaced with the same unmistakable all-zero placeholder
    used for `AesOptions`; `AesKeyRingResolver.EnsureActiveKeyIsNotKnownPlaceholder` (shared by both key rings)
    is now also called from `ProviderAccessTokenProtector`'s constructor alongside its existing `GetActiveKey`
    fail-fast, so a missing secret override for *this* key ring now also fails startup loudly outside
    Development instead of silently serving traffic under the known-bad key. As with R-019, recommendation (1)
    — out-of-band confirmation against the live `Secret` — remains outside what this remediation pass could
    perform, which is why this is Mitigated rather than Closed; likelihood is left unchanged at 20% for the same
    reason. See R-019's 2026-08-02 history entry for the full mechanism (identical for both key rings) and the
    same outstanding future-audit verification recommendation (a non-Development fail-fast integration test).
  - 2026-08-04 — claude-code-ai-review-4: mitigation independently re-verified and **confirmed genuine** for
    this key ring specifically — `ProviderAccessTokenProtector.cs:53` calls
    `AesKeyRingResolver.EnsureActiveKeyIsNotKnownPlaceholder(activeKey, "ProviderTokenProtection")` inside the
    existing `!IsDevelopment()` guard, so a deployment missing this secret's override now fails startup rather
    than silently protecting cached provider tokens under the publicly-committed key. Status and likelihood
    unchanged at Mitigated/20% for the same reason as R-019: the out-of-band question of whether the previously
    committed value was ever live in production remains unanswerable from repository content.

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
