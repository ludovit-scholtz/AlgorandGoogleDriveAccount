# Biatec MCP Server / OIDC Provider — Security Audit Report (Fourth Audit)

**Report file:** `audit-report-2026-04-08-34459ac-claude-code-ai-review-4.md`
**Auditor signature:** `claude-code-ai-review-4`

---

## 1. Front matter

### 1.1 Auditor

Performed by an AI coding assistant (Claude Code, Opus 5) operating with full whitebox access to the
`AlgorandGoogleDriveAccount` repository, at the request of the repository owner (Scholtz & Company, j.s.a.).

### 1.2 Conflict-of-interest disclosure

**This is not an independent third-party audit, and must not be represented as one.**

- The reviewing party is the same class of tool (and, for the three prior reports in this folder, the same
  tool) used by the development team to write parts of the code under review. `AUDITS-INSTRUCTIONS.md` §
  "Independence and conduct" requires that auditors be independent of the development of the feature being
  audited. That requirement is **not met** here: some of the code reviewed in this pass was authored by
  earlier sessions of the same assistant.
- No employment, equity, or consulting relationship exists in the conventional sense; the disclosure above is
  the material one.
- No dynamic testing was performed against any running Biatec deployment. No production or stage system was
  touched, no real user data or funds were accessed, and no authorization for such testing was sought or
  granted. All dynamic evidence in this report was produced locally, in this repository's own test project,
  against locally-generated throwaway keys.

Users and integrating third parties should treat this report as **structured first-party review**, useful for
prioritizing engineering work, and **not** as external assurance. The standing recommendation carried by every
prior report in this folder — that an independent firm re-verify these findings before the registry's
statuses are relied on externally — is repeated here and is now four audits old.

### 1.3 Commit(s) audited

- **Final commit in scope:** `34459ac` ("feat: Implement multisig tools with validation and transaction
  handling in BiatecMCP"), branch `master`, working tree clean at review time.
- **Range in scope:** `69d410c..34459ac` — 35 commits. `69d410c` is the commit reviewed by the third audit
  ([audit-report-2026-01-08-69d410c-claude-code-ai-review-3.md](audit-report-2026-01-08-69d410c-claude-code-ai-review-3.md)).
  163 files changed, ~23,700 insertions.
- Pre-existing code outside that range was reviewed only where a new finding touches it (notably
  `AlgorandTransactionInspector.cs` and `WalletService.cs`, whose *pricing* logic predates the range but whose
  gaps are newly reachable across four additional chain families added within it).

### 1.4 Dates of engagement

Single-session engagement, 2026-08-04.

### 1.5 Scope statement and deviations

The scope section of [AUDITS-INSTRUCTIONS.md](AUDITS-INSTRUCTIONS.md) still names the pre-restructure file
layout (`AlgorandGoogleDriveAccount/BusinessLogic/DevicePairingService`, `GoogleDriveRepository`,
`BiatecMCPGoogle.cs`, `build-api.yml`, …). Those files no longer exist. As the second and third audits did,
this pass mapped the *intent* of that scope onto the current three-project layout:

| Instructions' scope item | Reviewed here as |
| --- | --- |
| `BusinessLogic/` services | `BiatecOIDC/BusinessLogic/*`, `BiatecSelfCustodyCore/BusinessLogic/*` |
| `Controllers/` | `BiatecOIDC/Controllers/{Wallet,JwtIssuer,VaultBackup,Chains}Controller.cs` |
| `AesEncryptionHelper.cs`, `RedirectUriMatcher.cs` | unchanged in range; spot-checked only |
| `GoogleDriveRepository.cs` | `BiatecSelfCustodyCore/Repository/CloudAccountRepository.cs` + provider implementations |
| `MCP/BiatecMCPGoogle.cs` | `BiatecMCP/MCP/BiatecMCP.cs` (20 tools) |
| Config surfaces (`Model/*.cs`, `JwtIssuer:Clients`, `AesOptions`) | plus `CloudServices:Mock`, `k8s/main/conf-*`, `k8s/stage/conf-*` |
| `build-api.yml`, `docs/KUBE_CONFIG_SECURITY.md` | `deploy-stage.yml`, `promote-production.yml` (unchanged in range) |
| Dependency review | `dotnet list package --vulnerable --include-transitive`; ARC-76 package split reviewed |

**Deviations, stated explicitly:**

- **No dynamic testing** against a deployed instance (see §1.2). Every finding is derived from source review;
  where a finding was reducible to a local computation, a proof-of-concept was executed (see §3).
- **Prioritized, not exhaustive.** With ~23,700 lines added since the last review, this pass prioritized the
  *value-moving* surface: transaction inspection and spending-limit enforcement across the four chain
  families now supported (AVM, EVM, BTC, BCH), the address-activation trust boundary, and the new mock
  authentication path. Areas reviewed more lightly, and explicitly **not** cleared by this report: the Aramid
  bridge fee arithmetic, the DEX-quote aggregation providers, `BiatecMCP`'s Bitcoin coin-selection logic,
  `chains.html`, and the NSwag-generated `OidcApiClient.g.cs`.
- **Cryptographic primitives were not re-derived.** AES-GCM/AES-CBC usage, ARC-76 derivation, and the Ed25519/
  secp256k1 signing paths were reviewed for *correct application* (key lifetime, key reuse, IV handling as
  already covered by R-002), not re-analyzed as primitives.

### 1.6 Methodology summary

See §2 for detail. Static whitebox source review of the full diff, targeted threat modeling of the
spending-limit control against a hostile relying party, one executed proof-of-concept in the repository's own
test harness, dependency vulnerability scan, and a full run of both existing test suites (700 tests, all
passing) as a regression baseline.

### 1.7 Verdict

**Pass with findings.**

Terms as used in this report (there is no fixed rubric across firms; these are this report's definitions):

- **Pass** — no finding rated Medium or above; the system's stated security claims hold as documented.
- **Pass with findings** — the core self-custody claim (Biatec cannot unilaterally recover, decrypt, or
  exfiltrate a user's keys) continues to hold, and no finding permits key extraction or identity forgery; but
  one or more controls the system advertises do not hold as advertised, and should be fixed before those
  controls are relied upon.
- **Fail** — a finding permits key exfiltration, identity forgery, or unauthorized signing by a party who
  should not be able to sign at all.

The verdict is **not** Fail: nothing found in this pass lets Biatec, an infrastructure attacker, or an
unauthorized third party obtain key material or sign without a legitimately-issued `sign`-scoped token. The
verdict is **not** Pass because the **daily/weekly/monthly spending limit — the single control that bounds
what a legitimately-authorized but malicious or compromised relying party can take from a user — is
bypassable in at least four distinct ways** (§4, H-01, H-02, M-01, M-02, M-03), two of which require nothing
more exotic than setting one field the API already accepts.

---

## 2. Methodology

**What was reviewed.** The complete `69d410c..34459ac` diff, read file-by-file for the security-relevant
subset; then, independently of the diff, the end-to-end path a value-moving request takes today:
`POST /wallet/{network}/{address}/sign` → `WalletController.SignTransactionGroup` → family dispatch →
`AlgorandTransactionInspector` / `EvmTransactionRequestParser` / `BitcoinUnsignedTransaction` →
`WalletService` (valuation + limit check) → `SpendingLimitService` → `DriveService` (key load, sign, discard).
That path was traced once per chain family, since each family entered the codebase separately and none of
them shares the others' enforcement code.

**How.**

1. *Diff review* — `git diff 69d410c..34459ac`, prioritized by file (controllers, services touching key
   material or authorization, configuration, Kubernetes manifests).
2. *Control-centric threat modeling* — for the spending limit specifically, the adversary modeled is **a
   relying party holding a validly-issued `sign`-scoped access token** (a compromised integrator, a malicious
   MCP client, or an AI agent under prompt injection). This is precisely the adversary the limit exists to
   bound; anything the limit does not see is, for that adversary, unlimited. Each chain family's request DTO
   was examined for fields that move value but are not read by the valuation code.
3. *Proof of concept* — one PoC was written and executed in `BiatecOIDCTests` (then removed; it is reproduced
   verbatim in §4 H-01 so any reader can re-run it). It verifies both halves of the claim: that the inspector
   prices the transaction at zero, and that the value-moving field survives the decode/sign/re-encode round
   trip `DriveService` performs — i.e. that the signature Biatec returns is actually usable on-chain.
4. *Dependency scan* — `dotnet list Biatec.slnx package --vulnerable --include-transitive`: **no vulnerable
   packages reported**, which closes R-022 (see §5).
5. *Regression baseline* — `dotnet test` on both suites: **700 tests, 0 failures** (405 `BiatecOIDCTests`,
   295 `BiatecMCPTests`). Note that a passing suite is evidence of no regression, not of the absence of the
   findings below; none of the 700 tests exercises any of the bypasses in §4, which is itself a finding
   (M-05).
6. *Prior-finding re-verification* — every entry the third audit and the 2026-08-02 remediation pass touched
   was re-checked against the current code, not against the remediation pass's own description of itself (§5).

**Tools.** `git`, `ripgrep`, .NET SDK 10 (`dotnet build` / `dotnet test` / `dotnet list package
--vulnerable`), .NET reflection against the pinned `Algorand4` assembly (to confirm which transaction fields
the SDK models and therefore preserves across a sign round trip), and manual source reading. No commercial
SAST product, no fuzzing, no dynamic/DAST testing.

---

## 3. Threat model note: what the spending limit is for

Five of this report's findings concern one control, so it is worth stating plainly what that control is and
who it defends against, because the severity of the findings follows directly from it.

Biatec's self-custody design means a relying party never holds the user's key. What it holds is a
`sign`-scoped access token, and with it, the ability to ask Biatec to sign transactions. The **only** thing
standing between "an RP the user authorized once" and "an RP that drains the account" is the
daily/weekly/monthly spending limit enforced in `WalletService`/`SpendingLimitService`. This is stated as a
headline property in `OIDC_INTEGRATION_GUIDE.md` and in the wallet API's own documentation.

Therefore: **any value-moving construct the valuation code does not price is, for a hostile RP, an unlimited
withdrawal.** The user's configured limit does not merely fail to apply — the user has been told a limit
applies. Findings H-01, H-02, M-01, M-02 and M-03 are all instances of this one shape.

---

## 4. Detailed findings

Severity is assigned on impact-to-the-user, assuming the adversary in §3 (a legitimately-authorized but
hostile `sign`-scoped client). "High" here means: complete defeat of a control the user was told protects
them, with direct loss of funds, requiring no privileged position and no cryptographic weakness.

---

### H-01 — Spending limits are completely bypassed by a `close-remainder-to` payment (Algorand mainnet)

**Severity:** High
**Affected component:** [`BiatecOIDC/Helper/AlgorandTransactionInspector.cs:122-127`](../BiatecOIDC/Helper/AlgorandTransactionInspector.cs#L122-L127),
[`BiatecOIDC/BusinessLogic/WalletService.cs:69-95`](../BiatecOIDC/BusinessLogic/WalletService.cs#L69-L95)
**Status:** Open (new)
**Registry entry:** R-024

**Description.**

`AlgorandTransactionInspector.Inspect` determines a payment's value by reading exactly one wire field, `amt`
(`PaymentAmountKey`, line 67). Algorand payments carry a second, independent value-moving field:
`close` (`CloseRemainderTo`). A payment with `close` set transfers the sender's **entire remaining balance**
to the named address, after the `amt`/fee are applied — `amt` may be zero. It is a standard protocol feature,
not an obscure one; it is how accounts are closed out.

The inspector never reads `close`. `Kind` is `Payment` and `Amount` is `0`, so `WalletService`'s pricing loop
(line 76, `GetUsdValueAsync(info.AssetId, info.Amount)`) values the transaction at **$0.00**, `totalUsd`
stays `0`, the `if (totalUsd > 0m)` guard at line 92 skips `EnsureWithinLimitsAsync` entirely, no ledger
entry is written, and the transaction is signed. The user's daily limit is neither consulted nor decremented.

Two adjacent controls do not help:

- The `sender_mismatch` check (`WalletController.cs:178-186`) passes — the sender genuinely *is* the user's
  own address; that is the point.
- The `rekey` claim gate (`WalletController.cs:193-201`) does not fire — `close` is not `rekey`, and
  `IsRekey` is `false`.

The endpoint accepts caller-supplied base64 msgpack directly (`WalletController.cs:144`). A caller is under no
obligation to have built those bytes with `BiatecMCP`'s builder, so "our builder never sets `close`" is not a
control.

**Proof of concept (executed).**

The following test was added to `BiatecOIDCTests`, run against commit `34459ac`, and then removed. It asserts
both that the transaction prices at zero *and* that `close` survives the exact decode → `Sign` → re-encode
sequence `DriveService.SignTransactionAsync` performs (i.e. Biatec returns a signature that is valid on-chain
for the sweep):

```csharp
[Test]
public void Poc_CloseRemainderToPayment_IsPricedAsZero_AndSurvivesTheSigningRoundTrip()
{
    var me = new Account();
    var attacker = new Account().Address;
    var pay = new PaymentTransaction
    {
        Sender = me.Address,
        Receiver = attacker,
        Amount = 0,                      // limit sees this
        CloseRemainderTo = attacker,     // this actually moves the whole balance
        Fee = 1000, FirstValid = 1, LastValid = 1000,
        GenesisId = "mainnet-v1.0", GenesisHash = new Digest(new byte[32])
    };

    var wire = Encoder.EncodeToMsgPackOrdered(pay);
    var info = AlgorandTransactionInspector.Inspect(wire);

    // Exactly what DriveService.SignTransactionAsync does with caller-supplied bytes:
    var signed = Encoder.DecodeFromMsgPack<Transaction>(wire).Sign(me);
    var txnMap = (Dictionary<object, object>)MessagePackSerializer
        .Deserialize<Dictionary<object, object>>(Encoder.EncodeToMsgPackOrdered(signed), MapOptions)["txn"];

    Assert.That(info.Amount, Is.EqualTo(0UL));
    Assert.That(txnMap.ContainsKey("close"), Is.True);
}
```

Observed output:

```
INSPECT: Kind=Payment Amount=0 Sender=EKZCNN3JMIJHHHV4JKSEJHW4MHL5FEX4ORHW3I6CK2QARXXHBVFVQPYUYY IsRekey=False
SIGNED TXN KEYS: close,fee,fv,gen,gh,lv,rcv,snd,type
close present in signed output: True
```

**End-to-end reproduction** (not executed — would require a live deployment and is out of this engagement's
authorization): obtain any `sign`-scoped token for an account with a configured daily limit; `POST
/wallet/algorand-mainnet/{address}/sign` with the base64 of the bytes above; observe a 200 with a signed
transaction, and that `GET /wallet/limits` shows no spend recorded.

**Impact.** Total loss of the ALGO balance of any address a hostile `sign`-scoped RP can name, on Algorand
mainnet, regardless of the user's configured limits, in a single request. The user has been told a limit
applies. Note the balance is swept in one transaction, so limit *windows* offer no partial protection either.

**Recommended remediation.**

1. In `AlgorandTransactionInspector`, read the `close` key. Because the swept amount is not knowable from the
   transaction alone (it depends on the account's live balance), the correct treatment is **not** to price it
   — it is to **fail closed**: surface an `IsCloseOut` flag and have `WalletController` reject any `pay` with
   `close` set unless the token carries a dedicated, high-privilege claim (the `rekey` claim is the existing
   precedent for "permanently destructive, needs its own scope"), or unless the account's live balance is
   fetched and priced.
2. Apply the same treatment to the asset variant (`aclose`, `AssetCloseTo`) for defense in depth. **Note:**
   the pinned `Algorand4` 4.4.1 `AssetTransferTransaction` type does *not* model `AssetCloseTo` (verified by
   reflection over the assembly), so an `aclose` field is currently dropped when `DriveService` re-encodes the
   decoded transaction — the asset variant is **not** presently exploitable. That is an accident of the SDK's
   object model, not a control, and would silently become exploitable on an SDK upgrade that adds the
   property. Do not rely on it.
3. Add regression tests for both fields (see M-05).

---

### H-02 — Bitcoin/Bitcoin Cash spending limits are bypassed by a caller-supplied `IsChange` flag, and by fee inflation

**Severity:** High
**Affected component:** [`BiatecSelfCustodyCore/Model/BitcoinUnsignedTransaction.cs:41-47`](../BiatecSelfCustodyCore/Model/BitcoinUnsignedTransaction.cs#L41-L47),
[`BiatecOIDC/BusinessLogic/WalletService.cs:151`](../BiatecOIDC/BusinessLogic/WalletService.cs#L151),
[`BiatecSelfCustodyCore/BusinessLogic/DriveService.cs:186-190`](../BiatecSelfCustodyCore/BusinessLogic/DriveService.cs#L186-L190)
**Status:** Open (new)
**Registry entry:** R-025

**Description.**

Bitcoin-family spending limits are computed as:

```csharp
var spendSatoshis = transaction.Outputs.Where(o => !o.IsChange).Sum(o => o.AmountSatoshis);
```

`IsChange` is a **plain boolean field on the wire DTO**, deserialized straight from the caller's base64 JSON
body (`WalletController.cs:363`). Nothing verifies that an output marked `IsChange` actually pays an address
the signer controls. `DriveService.SignBitcoinTransactionAsync` builds each output from
`BitcoinAddress.Create(output.Address, network)` (line 188) with no comparison against `myAddress`, which it
has computed two lines earlier (line 171) and uses only to reconstruct the *inputs'* scriptPubKeys.

So: a hostile RP sends one output, paying its own address, with `"isChange": true`. `spendSatoshis` is `0`,
`totalUsd` is `0`, both `if (totalUsd > 0m)` guards (lines 154, 161) are skipped, no limit is checked, no
ledger entry is recorded, and the transaction is signed.

**A second, independent bypass exists on the same path.** The implicit miner fee is `sum(Inputs) −
sum(Outputs)`, and nothing bounds it. A caller can supply the account's entire UTXO set as inputs and a single
1-satoshi output, burning the whole balance to fee. This is priced at ~$0 and passes the limit trivially. It
requires the attacker to be (or to collude with) a miner to *capture* the funds, but as a **destructive** attack
it needs no collusion at all. The model's own doc comment ("the implicit fee is simply `sum(Inputs) −
sum(Outputs)`") documents the gap without bounding it.

The `BitcoinUtxoInput` design correctly refuses to trust a caller-supplied scriptPubKey, reconstructing it
from the signer's own derived address instead — a good decision. `IsChange` is the same class of trust
decision, made the other way.

**Reproduction** (source-level; no live BCH/BTC node was used, consistent with the repository's own caveat
that Bitcoin-family transfers have never been verified against a live node):

1. Hold a `sign`-scoped token for an account with a configured daily limit and a funded BTC address.
2. `POST /wallet/bitcoin/{address}/sign` with body
   `{"transactions":["<base64 of {\"inputs\":[…the account's UTXOs…],\"outputs\":[{\"address\":\"<attacker>\",\"amountSatoshis\":<all minus fee>,\"isChange\":true}]}>"]}`.
3. Observe 200 with a signed transaction; `GET /wallet/limits` records no spend.

**Impact.** Total loss of the BTC/BCH balance of any address a hostile `sign`-scoped RP can name, regardless
of configured limits. Additionally, unbounded destructive fee burn.

**Recommended remediation.**

1. Ignore the caller's `IsChange` entirely at the enforcement boundary. In
   `WalletService.SignBitcoinTransactionGroupAsync`, derive the signer's own address (the same
   `DeriveBitcoin[Cash]AddressAsync` the controller already has access to) and treat an output as change
   **only** if its `Address` equals that derived address. Everything else is a spend.
2. Price the implicit fee (`sum(Inputs) − sum(Outputs) − change`) as spend, or reject a transaction whose fee
   exceeds a sane multiple of the estimated fee for its size.
3. Consider removing `IsChange` from the wire DTO altogether — a field whose only consumer is a security
   decision, and whose value the server can compute itself, should not be on the wire.

---

### M-01 — EVM signing has no spending-limit enforcement of any kind

**Severity:** Medium
**Affected component:** [`BiatecOIDC/BusinessLogic/WalletService.cs:113-133`](../BiatecOIDC/BusinessLogic/WalletService.cs#L113-L133),
[`BiatecOIDC/Controllers/WalletController.cs:271-325`](../BiatecOIDC/Controllers/WalletController.cs#L271-L325)
**Status:** Open (new)
**Registry entry:** R-026

**Description.** `SignEvmTransactionGroupAsync` goes straight from argument validation to `SignEvmTransactionAsync`
— no valuation, no limit check, no ledger entry. A `sign`-scoped token can therefore move unlimited native
value (and execute unlimited ERC-20 `transfer` calldata) on Ethereum, Gnosis, Arbitrum and Base.

This is **documented** — `chains.html`'s capability matrix shows EVM as sign-capable but not limits-capable,
and the method's own remarks say so. It is nonetheless a finding rather than an accepted design choice,
because the gap is not surfaced *at the point where the user configures a limit*: `PUT /wallet/limits`
accepts and stores a limit with no indication that it does not apply to four of the eight supported chains,
and `GET /wallet/limits` reports it back unqualified. A user who sets a $100/day limit reasonably believes it
constrains their EVM addresses. It does not.

**Impact.** Unlimited loss of native-token balances on four EVM chains for a hostile `sign`-scoped RP; and,
more broadly, a limit-reporting surface that overstates the protection actually in force.

**Recommended remediation.** Either implement EVM valuation (a price oracle for the native token plus
calldata-aware ERC-20 decoding), or — as an immediate, cheap mitigation — have `GET /wallet/limits` and
`GET /wallet/{network}/{address}/limits` return an explicit `enforcedOn` / `notEnforcedOn` chain list, and
document the gap in the integration guide's limits section rather than only in the capability matrix.

---

### M-02 — Spending limits are silently skipped on every AVM chain except Algorand mainnet, including real-value chains

**Severity:** Medium
**Affected component:** [`BiatecOIDC/Controllers/WalletController.cs:222`](../BiatecOIDC/Controllers/WalletController.cs#L222),
[`BiatecOIDC/BusinessLogic/WalletService.cs:67-96`](../BiatecOIDC/BusinessLogic/WalletService.cs#L67-L96)
**Status:** Open (new — behavior introduced within the audited range, commit `c5964ba`)
**Registry entry:** R-027

**Description.**

```csharp
var isAlgorandMainnet = string.Equals(resolvedNetwork.AvmChain?.GenesisId, AlgorandMainnetGenesisId, ...);
```

is passed as `applySpendingLimits`, and when false the entire pricing/limit block is skipped. The stated
rationale is sound as far as it goes — the Biatec Router prices assets only on Algorand mainnet, so pricing
elsewhere would fail every transfer closed with a confusing error, which is the real bug this change fixed.

The problem is the fallback direction. Voi mainnet and Aramid mainnet are **production chains carrying real
value**, and they are in the supported network list. On those chains a configured limit is not merely
unenforceable — it is silently ignored, with a 200 response indistinguishable from an enforced one. Choosing
"fail open" over "fail closed" for a fund-protection control deserves to be an explicit, documented,
user-visible decision rather than a genesis-id equality check.

**Impact.** A hostile `sign`-scoped RP faces no limit on Voi or Aramid mainnet. A user who has set limits has
no way to discover this from the API.

**Recommended remediation.** Distinguish "no value at risk" (testnets — safe to skip) from "value at risk but
unpriceable" (Voi, Aramid — should not silently skip). For the latter, prefer one of: reject with a clear
`limits_unenforceable_on_network` error when the account has a non-zero limit configured; or add a per-account
opt-in acknowledging the gap. At minimum, surface the affected chains in the limits response as in M-01.

---

### M-03 — Application calls and asset-config transactions are unpriced, so inner transactions escape the limit

**Severity:** Medium
**Affected component:** [`BiatecOIDC/Helper/AlgorandTransactionInspector.cs:122-127`](../BiatecOIDC/Helper/AlgorandTransactionInspector.cs#L122-L127)
**Status:** Open (new)
**Registry entry:** R-028

**Description.** Every transaction type other than `pay`/`axfer` maps to `AlgorandTransactionKind.Other`,
priced at `0`. That includes `appl` (application call). On Algorand, an application call can emit **inner
transactions** moving arbitrary amounts of ALGO and any ASA out of the caller's account — this is how every
DeFi interaction on the chain works. It also includes `acfg`, which can reassign an asset's clawback/manager
addresses, enabling a later drain that never passes through this endpoint at all.

So the limit bounds direct transfers, but not the far more common (and equally value-moving) mechanism of
calling a contract. A hostile RP does not need H-01 if it can simply deploy and call its own application.

Unlike H-01, this one is genuinely hard to price correctly — the value moved by an app call is not knowable
without simulating it. The finding is that the gap is currently **invisible**: the transaction is accepted and
recorded as a $0 spend rather than flagged.

**Impact.** The spending limit does not bound the most general value-moving transaction type on the chain.

**Recommended remediation.** Options, in increasing order of effort: (a) surface `Kind == Other` in the
response and the ledger as "unpriced", so the user's spend history is not silently wrong; (b) require a
distinct claim/scope for `appl` transactions, so a user can grant "payments only"; (c) use algod's
`/v2/transactions/simulate` endpoint to obtain the inner-transaction set and price that. (c) is the correct
long-term answer and is a substantial piece of work.

---

### M-04 — A read-only-scoped token can force unbounded writes to the user's cloud storage; activation registry has no concurrency control

**Severity:** Medium
**Affected component:** [`BiatecOIDC/Controllers/WalletController.cs:606-677`](../BiatecOIDC/Controllers/WalletController.cs#L606-L677),
[`BiatecOIDC/BusinessLogic/AddressActivationService.cs:53-77`](../BiatecOIDC/BusinessLogic/AddressActivationService.cs#L53-L77)
**Status:** Open (new)
**Registry entry:** R-029

**Description.** Two related problems on the same endpoint.

*(a) Unbounded write amplification from an identity-only token.* `GET /wallet/address/{seedAddress}/{slot}` is
documented and gated as read-only — `TryAuthenticate(requiredClaim: null, …)`, i.e. any validly-authenticated
caller, no `sign` and no `manage-limits`. But it performs **up to four sequential
load-decrypt-modify-encrypt-upload cycles** against the user's own Drive/OneDrive (lines 632-657), one per
chain family. `slot` is an unconstrained `int` supplied by the caller, and every distinct slot yields four new
registry entries. A client holding nothing but an `openid` token can therefore iterate slots and grow
`AddressActivations.%AESID%.dat` without bound.

Because `ResolveSignerAsync` (line 1202) loads and decrypts that whole file on **every** sign, limits, and
info call, an inflated registry degrades and eventually breaks every wallet operation for that user — a
denial of service against the user's own wallet, mounted from the lowest-privilege token the system issues,
with no rate limiting anywhere in the path. It also consumes the user's Drive quota and produces write
traffic that may trip the storage provider's own throttling.

*(b) No concurrency control.* `ActivateAsync` is a bare read-modify-write with no equivalent of
`CloudAccountRepository.SaveVaultWithConcurrencyCheckAsync` (the mitigation added for R-021). Two concurrent
activations — including the four issued back-to-back by a *single* `GetAddress` call, should the user have
two clients calling it at once — can silently lose an entry. A lost entry means the affected address stops
resolving (`address_not_active`) until re-derived. Recoverable, not fund-losing, but it is exactly the defect
R-021 was opened for, in a file R-021's fix does not cover.

**Impact.** (a) Availability of the wallet API for a targeted user, from a minimum-privilege token.
(b) Silent loss of activation entries, causing signing to fail for an externally-rekeyed address until
re-activated.

**Recommended remediation.** (a) Bound `slot` to a sane range (the ARC-76 use case does not need 2³¹ slots);
cap the number of entries in the registry; add per-user rate limiting on the derive endpoint. Consider
requiring a write-ish claim for an endpoint that writes. (b) Apply the same baseline-bytes concurrency check
`SaveVaultWithConcurrencyCheckAsync` uses, or batch the four activations of one `GetAddress` call into a
single load/save.

---

### M-05 — No test coverage for any spending-limit bypass class

**Severity:** Medium (process finding)
**Affected component:** `BiatecOIDCTests/AlgorandTransactionInspectorTests.cs`, `BiatecOIDCTests/WalletServiceTests.cs`
**Status:** Open (new)
**Registry entry:** folded into R-024 (see registry History)

**Description.** The inspector's test file covers 19 cases — amount decoding, integer widths, rekey detection,
multisig unwrapping, malformed input. Not one asserts what happens to a transaction that moves value through
a field the inspector does not read. The same is true of `WalletServiceTests`. The 700-test suite passes
against a codebase with H-01 and H-02 live in it.

This matters beyond the individual bugs: the enforcement code's tests are all written from the perspective of
"does it correctly price what it knows about", never "what can move value without it noticing". That framing
is why four chain families were added without anyone noticing that three of them enforce nothing.

**Recommended remediation.** Add negative tests per chain family asserting that a limit is *enforced* for
each value-moving construct: `close`/`aclose`, `appl`, EVM value transfer, Bitcoin non-change output and fee.
Where a construct is deliberately unenforced, the test should assert the *documented* behavior explicitly, so
the gap is visible in the suite rather than invisible.

---

### L-01 — A configuration-gated authentication bypass ships in the production binary

**Severity:** Low
**Affected component:** [`BiatecOIDC/Controllers/JwtIssuerController.cs:804-836`](../BiatecOIDC/Controllers/JwtIssuerController.cs#L804-L836),
[`BiatecSelfCustodyCore/Providers/MockCloudStorageProvider.cs`](../BiatecSelfCustodyCore/Providers/MockCloudStorageProvider.cs),
[`BiatecOIDC/Program.cs:96-102,155-159,397-421`](../BiatecOIDC/Program.cs#L96-L102)
**Status:** Open (new)
**Registry entry:** R-030

**Description.** `MockSignIn` is `[AllowAnonymous]`, takes no credential, and signs the browser into a full
cookie session as a configured synthetic identity — then hands off to the *real* `AuthorizeCallback`, so the
resulting OIDC code and access token are indistinguishable from a genuine sign-in. `MockCloudStorageProvider`
reports `IsConfigured => true` unconditionally. The seed vault for those identities is created from
**mnemonics stored in plaintext configuration** (`CloudServices:Mock:Accounts[].Mnemonic`).

The gate is entirely configuration: the provider is only registered when `CloudServices:Mock:Enabled` is true
**and** at least one account is configured (`Program.cs:102`), and `SelectProvider` additionally hides the
button unless the `/authorize` request named a configured `scopeId`. That gating is well-constructed, and it
was verified that **no committed configuration enables it** — `appsettings.json` ships `"Enabled": false`
with an empty account list, and no `k8s/main/*` or `k8s/stage/*` manifest mentions `Mock` at all.

The finding is the residual risk of the shape itself: a complete authentication bypass exists in the shipped
production artifact, one environment variable (`CloudServices__Mock__Enabled=true` plus one account) away
from being live, and its blast radius is total (sign in as any configured identity, obtain `sign`-scoped
tokens, spend that vault). Compare the treatment given to placeholder AES keys, where the fix for R-019/R-023
was precisely to make the dangerous configuration *impossible to activate accidentally in production* rather
than merely unset by default.

**Impact.** If ever enabled in a production or stage deployment — by misconfiguration, by an attacker with
ConfigMap/Secret write access, or by an operator debugging an incident — anyone who can reach `/authorize`
obtains full wallet access to the configured mock identities' vaults. Contained to those synthetic
identities; it does not grant access to real users' vaults.

**Recommended remediation.** Apply the R-019/R-023 precedent: fail fast at startup if
`CloudServices:Mock:Enabled` is true while `IHostEnvironment` is not `Development`, so the bypass is
structurally unreachable in production rather than merely off by default. Alternatively, compile the mock
provider out of Release builds behind a `#if` / a separate `Debug`-only package reference.

---

### L-02 — `POST /wallet/{btc|bch}/{seedAddress}/{slot}/activate` cannot succeed and reports a misleading error

**Severity:** Low
**Affected component:** [`BiatecOIDC/Controllers/WalletController.cs:844-875`](../BiatecOIDC/Controllers/WalletController.cs#L844-L875)
**Status:** Open (new); registry entry not opened (functional defect with no security impact — recorded here only)

**Description.** `ActivateAddress` branches on `Family == Avm` to derive an AVM address, and takes the `else`
branch — `DeriveEvmAddressAsync` — for *every other family*, including `Btc` and `Bch`. A Bitcoin address can
therefore never equal the "derived" address it is compared against, so control falls through to the on-chain
rekey check, which dereferences `resolvedNetwork.AvmChain!` — `null` for a Bitcoin-family network. The
resulting exception is swallowed by the `catch (Exception)` at line 871 and reported as
`503 algod_unavailable`, telling the caller to retry something that can never succeed.

**Impact.** No security impact (the endpoint fails closed — nothing incorrect is ever registered). Confusing
error, and Bitcoin-family addresses cannot be explicitly activated (they are activated automatically by
`GET /wallet/address/{seedAddress}/{slot}`, so nothing is actually blocked).

**Recommended remediation.** Reject `Btc`/`Bch` at the top of `ActivateAddress` with a clear
`400 unsupported_family` ("Bitcoin-family addresses have no rekey concept; they are activated automatically
when derived"), matching the explicit handling EVM already gets at line 857.

---

### Investigated and found NOT to be issues

Recorded because a skeptical reader would otherwise reasonably wonder.

- **Asset close-out (`aclose`) is not currently exploitable.** The natural companion to H-01 would be an
  `axfer` with `AssetCloseTo` set, sweeping an entire ASA holding. Reflection over the pinned
  `Algorand4 4.4.1` assembly confirms `AssetTransferTransaction` exposes no `AssetCloseTo` property, so the
  field is dropped when `DriveService` decodes and re-encodes the caller's bytes; the returned signature does
  not carry it. This is a property of the SDK's object model, not of Biatec's code, and would regress
  silently on an SDK upgrade — hence it is still listed as remediation item 2 under H-01.
- **Multisig envelopes skipping the `sender_mismatch` check is not exploitable.** `WalletController.cs:178`
  deliberately skips the sender check for multisig envelopes. A hostile caller cannot use this to obtain a
  usable single-signature over an arbitrary transaction: signing inside a multisig envelope produces a
  `MultisigSubsig`, valid only against the multisig account whose participants and threshold produced the
  address, not a bare signature for the user's own account. Spending-limit pricing still applies, since
  `Inspect` unwraps `txn` before reading the amount.
- **The audience-validation change (`d892e90`) does not weaken token validation.**
  `ValidateBearerAccessToken` now accepts `aud` values from `Current.Clients ∪ Current.ProtectedResources`.
  The resource URI is only ever placed in `aud` by this server, at issuance, after validating the requested
  `resource` against the configured allowlist (`CreateAccessToken`, RFC 8707), so the "reject a deregistered
  or tampered client" property is preserved. A dynamically-registered client's token still cannot reach the
  wallet API unless that token was issued for an allowlisted resource.
- **Bitcoin input scriptPubKeys are correctly not trusted from the wire.** `DriveService` reconstructs each
  input's scriptPubKey from the signer's own derived address
  (`BiatecSelfCustodyCore/BusinessLogic/DriveService.cs:183`), so a caller cannot induce a signature over
  someone else's UTXO. This is the right decision and is called out as a contrast to H-02.
- **`CloudAccountRepository`'s `SeedAddress` self-healing does not introduce a substitution risk.**
  `HealMissingSeedAddressesAsync` recomputes the address from the entry's own mnemonic using the identical
  derivation `BuildSeedEntry` uses, and only when the stored value is empty — it cannot overwrite a
  legitimate address with an attacker-influenced one.
- **The mock provider is not enabled in any committed configuration.** Verified across `appsettings.json`,
  `k8s/main/conf-*`, and `k8s/stage/conf-*`. See L-01 for the residual concern.
- **No vulnerable dependencies.** `dotnet list Biatec.slnx package --vulnerable --include-transitive` reports
  none; the `Microsoft.IdentityModel.*` / `System.IdentityModel.Tokens.Jwt` packages are now pinned at
  `8.21.0`, well clear of the 5.5.0 train that carried GHSA-59j7-ghrg-fj52 (R-022).

---

## 5. Remediation tracking

Status of every item raised by the third audit and the 2026-08-02 engineering remediation pass, verified
against the code at `34459ac` rather than against the remediation pass's own description of itself.

| Prior item | Claimed status | Verified at `34459ac` | Evidence |
| --- | --- | --- | --- |
| **R-020** — vault-backup OAuth CSRF | Fixed | **Confirmed fixed** | `VaultBackupController.Authorize`/`Callback` both call `EnsureBrowserOwnsBackup`, which requires the ambient cookie session to be the *same account* the pending backup belongs to — correctly identified in the code's own remarks as the necessary check (an anti-CSRF cookie alone would not have closed it, since the victim's browser genuinely completes both legs). |
| **R-021** — seed-vault write race | **Partially fixed** | `SaveVaultWithConcurrencyCheckAsync` present and used by `CreateSeedAsync`, `SwitchPrimarySeedAsync`, `SeedTestVaultAsync`, and the first-seed branch of `LoadVaultEnsuringAtLeastOneSeedAsync`; surfaces `409 vault_concurrency_conflict`. Correctly documented as best-effort, not a provider-enforced CAS. **However** the same defect exists unfixed in `AddressActivationService` — see M-04(b). | `CloudAccountRepository.cs:655-670`; `AddressActivationService.cs:61-74` |
| **R-022** — `Microsoft.IdentityModel.*` 5.5.0 advisory | Fixed | **Confirmed fixed / closeable** | Packages pinned at `8.21.0`; `dotnet list package --vulnerable --include-transitive` reports nothing. |
| **R-018** — MCP tools leak raw exception text | Fixed | **Confirmed fixed** | `BiatecMCP.cs` routes generic `catch (Exception)` through `SanitizeForToolResponse(ex, nameof(...))`; raw `ex.Message` now appears only for typed, caller-facing exceptions (`WalletApiException`, argument validation) whose messages are authored, not internal. |
| **R-019 / R-023** — committed AES / ProviderTokenProtection key material | Fixed | **Confirmed fixed** | Committed ConfigMaps now carry unmistakable all-zero placeholders; `AesKeyRingResolver.EnsureActiveKeyIsNotKnownPlaceholder` rejects both the new all-zero values and the two historical real-looking ones. Verified it is invoked for **both** key rings — `CloudAccountRepository.cs:56` (`AesOptions`) and `ProviderAccessTokenProtector.cs:53` (`ProviderTokenProtection`) — and only under the `!IsDevelopment()` guard. |
| **R-013** — CI/CD gate; `k8s/main/conf` contents | Open | **Still open, unchanged** | No change in range to `deploy-stage.yml` / `promote-production.yml`. GitHub branch-protection settings remain unverifiable from repository content alone. Likelihood left at 5%. |
| **R-004** — MCP broadcast with no confirmation gate | Closed (2026-07-24) | **Re-opened as a concern, not as an entry** | Not re-opened, but noted: the audited range consolidated two broadcast tools into one (`submitTransactionToBlockchain`) and added `ServerInstructions` after an observed incident in which a connected agent bypassed this server entirely and submitted an already-signed transaction to a third-party node. That incident is evidence that the *signing* boundary, not the broadcast boundary, is the only enforceable one — which is precisely why H-01/H-02 matter. No registry change. |

---

## 6. Risk registry changes

Applied to [RISKS.md](RISKS.md) by this audit, signature `claude-code-ai-review-4`, date 2026-08-04:

**Added (7 new entries):**

| ID | Title | Likelihood | Justification summary |
| --- | --- | --- | --- |
| R-024 | Spending limit bypassed by `close-remainder-to` payment | 45% | Trivial to exploit, no special position needed, PoC-verified; the whole population of `sign`-scoped RPs is the threat set, and the MCP surface deliberately exposes signing to third-party AI agents. Tempered below 50% only because it requires a *hostile or compromised* RP rather than an outsider, and Biatec's RP set is currently small and mostly first-party. |
| R-025 | Bitcoin/BCH limit bypass via caller-supplied `IsChange`; unbounded fee | 30% | Same exploit shape as R-024 but on chain families with no live-node verification yet and therefore, today, little real balance at risk. Rises sharply if BTC/BCH support sees real use. |
| R-026 | EVM signing entirely unmetered | 35% | Documented gap, but the limits API reports a limit that does not apply; EVM chains are the most liquid supported. Lower than R-024 only because it is a known/documented gap rather than a silent one. |
| R-027 | Limits silently skipped on non-mainnet AVM chains (Voi, Aramid) | 20% | Real-value chains, fail-open behavior, but materially smaller balances and RP population than Algorand mainnet or EVM. |
| R-028 | App-call/`acfg` transactions unpriced; inner transactions escape the limit | 40% | The most *general* bypass and requires no protocol trickery at all — any contract call. High likelihood, but the impact framing is "limit does not bound DeFi activity", which for many users is arguably intended behavior; genuinely hard to fix correctly. |
| R-029 | Low-privilege token can force unbounded cloud-storage writes; activation registry lacks concurrency control | 15% | Availability-only, self-inflicted-scope (the victim's own wallet), and requires a hostile client the user already authorized. No fund loss. |
| R-030 | Configuration-gated authentication bypass (mock provider) ships in the production artifact | 8% | Correctly gated, not enabled anywhere committed, and blast radius is limited to synthetic identities. Non-zero because the shape (one env var from total bypass) has a poor track record industry-wide, and because stage/production share a deployment pipeline. |

**Revised:** R-021's entry gains a History line recording that its fix is confirmed genuine for the seed
vault but does **not** extend to `AddressActivationService` (tracked as R-029 rather than by re-opening
R-021, following the precedent set for R-011/R-018 by the second audit).

**Closed:** none newly closed by this audit. R-022 was already closed by the 2026-08-02 remediation pass; this
audit independently confirmed the closure (packages pinned at `8.21.0`, clean vulnerability scan) and recorded
that confirmation in its History rather than changing its status.

**Unchanged:** R-013 (still Open, 5%, no code or settings change in range). All entries in "Closed risks" and
"Accepted / unmitigable risks" (including R-017) were re-read and none were silently dropped.

---

## 7. Signature

**Auditor:** `claude-code-ai-review-4` — Claude Code (Opus 5), acting as an AI code auditor with whitebox
repository access, at the request of Scholtz & Company, j.s.a.

**Commit audited:** `34459ac` (range `69d410c..34459ac`)
**Date finalized:** 2026-08-04
**Test baseline at time of audit:** 700 tests passing (405 `BiatecOIDCTests`, 295 `BiatecMCPTests`), 0 failures.
**Dependency scan:** no vulnerable packages (direct or transitive).

No cryptographic attestation is provided over this file's hash. Per §1.2, this report is first-party review
and should not be published as independent third-party assurance. Per `AUDITS-INSTRUCTIONS.md` §
"Publication", findings H-01 and H-02 describe **currently unremediated** high-severity issues; coordinate
disclosure timing with the engagement owner before publishing this report externally.
