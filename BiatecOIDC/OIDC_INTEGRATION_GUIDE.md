# Biatec OIDC and JWT Integration Guide

This project now exposes a standards-oriented OpenID Connect style identity provider so other applications can delegate login to Biatec Google authentication and receive signed JWT tokens containing Algorand identity claims.

**Host**: `https://oidc.biatec.io` is the recommended host for new integrations — use it below. The
service is also still reachable at `https://google.biatec.io` (its original, shared host, kept
working as a legacy alias for existing integrations). Each host is internally self-consistent: the
`iss` claim and the discovery document's `issuer` field always match whichever host you actually
call — they are derived from the request, not hardcoded — so pick one host and use it consistently
for a given client (don't mix the two for the same integration).

This exact file is also what renders at the top of Swagger UI (`/swagger`) - `Program.cs`'s `AddSwaggerGen`
call reads it at startup and sets it as the OpenAPI document's `info.description`, so browsing `/swagger`
directly gets the full integration guide above the endpoint list, not just bare operation names.

## Goals

- Reuse Google login session from this service.
- Issue JWT tokens signed by Biatec signing key.
- Include Algorand address and email claims in issued tokens.
- Keep redirect handling allowlisted per application client.

## Implemented Endpoints

- `GET /.well-known/openid-configuration`
  - OIDC Discovery document for clients.
- `GET /.well-known/oauth-authorization-server`
  - RFC 8414 OAuth 2.0 Authorization Server Metadata - identical content to the OIDC discovery document
    above. Served alongside it because some OAuth/MCP clients (e.g. VS Code's MCP client) probe this URL
    first and only fall back to OIDC discovery on a 404; a spec-compliant client falls back correctly
    either way, but serving both avoids that round trip and is more broadly compatible with pure-OAuth
    (non-OIDC-aware) clients.
- `GET /.well-known/jwks.json`
  - Public signing keys for JWT validation.
- `GET /authorize`
  - Authorization endpoint.
  - Supports:
    - `response_type=code` (recommended, server to server token exchange)
    - `response_type=id_token` (legacy style direct token return)
  - Supports `response_mode=query` and `response_mode=form_post`.
  - Supports legacy `returnUrl` alias for `redirect_uri`.
  - Supports PKCE (`code_challenge`, `code_challenge_method`) per RFC 7636 — see "PKCE for Public Clients" below.
  - Supports `resource` (RFC 8707) — see "Dynamic Client Registration and resource indicators (for MCP-class
    clients)" below.
- `POST /token`
  - Token exchange endpoint.
  - Supports:
    - `grant_type=authorization_code` (accepts `code_verifier` for PKCE, `resource` for RFC 8707)
    - `grant_type=refresh_token`
- `POST /register`
  - RFC 7591 Dynamic Client Registration — self-registers a new **public** client (no secret is ever issued
    by this endpoint). See "Dynamic Client Registration and resource indicators (for MCP-class clients)" below.
- `GET /connect/endsession`
  - RP-initiated logout endpoint.
  - Also available as `GET /logout` alias.
  - Supports parameters:
    - `id_token_hint` (recommended)
    - `post_logout_redirect_uri` (recommended)
    - `state` (recommended)
    - `client_id` (recommended for compatibility)
- `GET /userinfo`
  - Returns user claims from bearer access token.
- `POST /introspect`
  - RFC-like active token introspection response.
- `POST /verify`
  - Convenience token verification endpoint.
- `POST /wallet/{network}/{address}/sign`
  - Signs a transaction group as `address` on `network` - both Algorand-family (AVM) and Ethereum-family
    (EVM) chains. Requires the `sign` scope. Body is just `{ "transactions": [...] }` now - see
    "Address-centric wallet API" below for how `address` resolves to a signing seed/slot and how this
    differs from the old `POST /wallet/sign`. For AVM, each entry is base64 msgpack, additionally requires
    the `rekey` scope if any transaction carries Algorand's `rekey` field (see "Wallet API" below), and is
    checked against the caller's spending limit. For EVM, each entry is base64 UTF-8 JSON (see "EVM
    (Ethereum-family) support" below for the shape) - no rekey concept, no spending-limit enforcement yet.
- `GET /wallet/address/{seedAddress}/{slot?}`
  - Derives (without signing anything) the address at `slot` (default `0`) for the seed identified by
    `seedAddress`, for **every currently-supported chain family in one call** - both the Algorand-family
    (AVM) address and the Ethereum-family (EVM) address, since there's no per-chain concept at this layer
    (an AVM address is genesis-independent, an EVM address is the same across every EVM chain). Only
    requires being authenticated. As a side effect, the AVM address becomes resolvable by address alone
    afterwards if `slot` is non-zero (a slot-0 AVM address already is, since it's the seed's own identifying
    address); the EVM address always does, since it's never a seed's own identifying address by itself. To
    list every seed's identifying address (rather than derive one slot), use `GET /wallet/seeds` instead.
- `GET /wallet/{network}/{address}/info`
  - Reports whether Biatec currently knows which key signs for `address` on `network` - a seed's own
    primary address, a previously-derived address (any slot), or one explicitly activated (below). Only
    requires being authenticated. See "Address-centric wallet API" below.
- `POST /wallet/{network}/{seedAddress}/{slot}/activate`
  - Registers that `seedAddress`/`slot`'s key signs for the address named in the body
    (`{ "address": "..." }`) - the entry point for rekeying an external AVM account to a
    Biatec-controlled key. Requires the `sign` scope. See "Address-centric wallet API" below for the full
    flow and verification rules.
- `GET /wallet/active-addresses`
  - Lists every address currently resolvable to a signing seed/slot - every seed's own slot-0 AVM address
    (active implicitly) plus every entry in the address activation registry (any non-zero AVM slot, every
    EVM address, and any externally-rekeyed AVM address). Only requires being authenticated. See
    "Address-centric wallet API" below.
- `GET /wallet/limits`
  - Reads the caller's own account-wide daily/weekly/monthly spending limits. Only requires being
    authenticated (`openid`) - no `manage-limits` scope needed to read your own limits.
- `PUT /wallet/limits`
  - Sets the caller's own account-wide daily/weekly/monthly spending limits and their currency. Requires
    the `manage-limits` scope.
- `GET /wallet/{network}/{address}/limits`
  - Reads the daily/weekly/monthly spending limits for the bucket tied to `address`, instead of the
    account-wide global bucket. Only requires being authenticated.
- `PUT /wallet/{network}/{address}/limits`
  - Sets the daily/weekly/monthly spending limits for the bucket tied to `address`. Requires the
    `manage-limits` scope.
- `GET /wallet/limits/currencies`
  - Lists every currency a spending limit can be configured in, with its current USD exchange rate. Only
    requires being authenticated.
- `GET /wallet/seeds`
  - Lists every Algorand seed ever generated for the caller (never the mnemonic itself - just each seed's
    identifying address, creation date, and whether it's primary). Only requires being authenticated.
- `POST /wallet/seeds`
  - Generates a brand-new, independent seed and adds it to the caller's vault - existing seeds are never
    removed. Requires the `rekey` scope (see "Multi-seed vault and rekey" below).
- `PUT /wallet/seeds/primary`
  - Switches which seed in the caller's vault is used for normal signing going forward. Requires the `sign`
    scope.
- `POST /wallet/backup/start`, `GET /wallet/backup/authorize`, `GET /wallet/backup/callback`,
  `POST /wallet/backup/complete`
  - Explicit, user-triggered copy of the encrypted vault to a second cloud provider (see "Cross-cloud vault
    backup" below). `start`/`complete` require the `sign` scope; `authorize`/`callback` are a browser round
    trip, not API calls.
- `GET /chains`
  - Public, unauthenticated - no bearer token required. Lists every Algorand-family chain this deployment
    currently considers usable: every chain published in the public
    [genesis list](https://scholtz.github.io/AlgorandPublicData/genesis/genesis-list.json) that also has at
    least one currently-live public algod node reporting the matching genesis hash right now (checked live
    against each chain's own `public-algod-providers.json`). See "Multi-chain support" below.

## Multi-chain support

`GET /chains` returns `{ "chains": [ { "genesisId", "name", "genesisHash", "algodApiAddress" }, ... ] }` -
one entry per currently-supported chain, sorted by nothing in particular (treat it as a set). Deliberately
does **not** include a node's own auth token/header - that's operational detail an external relying party
has no use for; if you need to call that chain's node yourself, use your own algod infrastructure or one of
the entries from that chain's own `public-algod-providers.json`.

A chain only appears here if it's both listed in the public registry *and* currently reachable - this is a
liveness snapshot (cached ~10 minutes server-side), not a static allowlist. Use it to validate a `genesisId`
before passing it to `POST /wallet/sign` or any other genesisId-accepting call, instead of hardcoding a list
that can silently go stale if a chain's public infrastructure changes.

## Address-centric wallet API

**Breaking change**: `POST /wallet/sign` and `GET`/`PUT /wallet/limits` used to take an optional
`seedAddress`/`slot` selector (a body field for sign, query params for limits) to pick which seed/slot
signs or owns a spending-limit bucket, defaulting to the vault's primary seed at slot 0. That selector is
gone - the *address itself* is now a route segment, alongside a `network` segment (an exact network code
like `algorand-mainnet`/`voi-mainnet` - see "Strict network codes" below, not a raw genesis id or a fuzzy
display name):

| Before | After |
|---|---|
| `POST /wallet/sign` with body `{ "transactions": [...], "seedAddress": "SEED", "slot": 5 }` | `POST /wallet/algorand-mainnet/{address}/sign` with body `{ "transactions": [...] }` |
| `GET /wallet/limits?seedAddress=SEED&slot=5` | `GET /wallet/algorand-mainnet/{address}/limits` |
| `PUT /wallet/limits?seedAddress=SEED&slot=5` | `PUT /wallet/algorand-mainnet/{address}/limits` |
| `GET`/`PUT /wallet/limits` (no selector - global bucket) | unchanged |
| `GET /wallet/address` (list) | removed - use `GET /wallet/seeds` instead (same data) |
| `GET /wallet/evm/address` (list) | removed - use `GET /wallet/{network}/{address}/info` to check a specific address, or `GET /wallet/seeds` + `GET /wallet/address/{seedAddress}/{slot?}` per seed |
| `GET /wallet/evm/address/{seedAddress}/{slot?}` (derive) | removed - `GET /wallet/address/{seedAddress}/{slot?}` now derives and returns both the AVM and EVM address in one call |
| `POST /wallet/{network}/{address}/activate` with body `{ "seedAddress": "SEED", "slot": 0 }` | `POST /wallet/{network}/{seedAddress}/{slot}/activate` with body `{ "address": "..." }` - `seedAddress`/`slot` are route segments now, the address being activated is the body field |
| *(no equivalent)* | `GET /wallet/active-addresses` (new) - lists every currently-active address in one call |

**Strict network codes**: `network` must exactly match one of a small, closed set of codes - AVM chains are
`{chain}-{variant}` (`algorand-mainnet`, `algorand-testnet`, `voi-mainnet`, `aramid-mainnet`, ...), EVM and
Bitcoin-family chains have no variant suffix (`ethereum`, `arbitrum`, `base`, `bitcoin`, `bitcoin-cash`). No
fuzzy matching, raw genesis id, display name, or numeric EVM chain id is accepted - an unrecognized value
fails with `400 unknown_network`. BiatecMCP's `listSupportedNetworks` tool enumerates the current full set if
you're integrating outside BiatecMCP and need to discover it programmatically.

`address` must be a **known** address - resolved to the seed/slot that actually signs for it via:

1. **A seed's own primary address** (its ARC-76 slot-0 address) - recognized for free, no extra step ever needed.
2. **A previously-derived address at any slot** - calling `GET /wallet/address/{seedAddress}/{slot}` derives
   *and registers* both the AVM and EVM address for that seed/slot, so either becomes usable by address
   alone from then on. This is the same call you'd make anyway to find out what the address even is, so in
   practice this never requires an extra step either.
3. **An explicitly activated address** - `POST /wallet/{network}/{seedAddress}/{slot}/activate` (note:
   `seedAddress` and `slot` are route segments here, not body fields - the address being activated is the
   body), body `{ "address": "..." }`. This is the entry point for **rekeying an external Algorand
   account to a Biatec-controlled key**: rekey the external account to one of this account's addresses
   (mint a fresh seed via `POST /wallet/seeds` if you want a dedicated one), submit and confirm that rekey
   transaction on-chain yourself, then call `/activate` naming which seed/slot now controls it. Biatec
   verifies this on-chain (checks the address's `auth-addr` against the derived address via algod) before
   accepting it - nothing is registered if the rekey hasn't actually confirmed yet (`409 rekey_not_confirmed`).
   For a *native* address (one that already exactly equals its seed/slot's derived address), this just
   registers it immediately, equivalent to step 2 - calling it is rarely necessary since deriving the
   address already does this.

This pairing (`address` → `seedAddress`/`slot`) is stored **encrypted on your own cloud drive** (Google
Drive/OneDrive, whichever you're signed in with), in a file separate from the seed vault itself - never on
Biatec's own infrastructure, same principle as the seed vault and spending-limit data.

`GET /wallet/{network}/{address}/info` reports one address's current status:
`{ "address", "network", "family", "isActive", "seedAddress", "slot" }` - `seedAddress`/`slot` are
`null`/`0` when `isActive` is `false`. `GET /wallet/active-addresses` reports every currently-active address
at once - `{ "addresses": [ { "address", "family", "seedAddress", "slot", "activatedUtc" }, ... ] }` -
combining every seed's own slot-0 AVM address (its `activatedUtc` is that seed's own creation date) with
every entry in the activation registry.

A plain (non-multisig) transaction's own `snd` (sender) field must match the route's `address` exactly, or
`POST /wallet/sign/...` fails with `400 sender_mismatch` - a defense-in-depth check to catch signing under
the wrong identity by mistake. This doesn't apply to a multisig co-signing envelope, where `address` is the
individual participant's own key, not the multisig group's address.

## EVM (Ethereum-family) support

Every account already has an Ethereum-family identity, not just an Algorand one - the same underlying seed
derives both, via the `ARC76Account.Ethereum` package's `ARC76.GetEmailAccount` (the Ethereum-family
counterpart of the `ARC76Account.Algorand` package's own `ARC76.GetEmailAccount`). No new consent flow or storage format was needed for this. Unlike
Algorand's `genesisId`-per-network split, there is **no per-EVM-chain concept at this API layer** - one EVM
address (per seed/slot) is valid across every EVM chain (Ethereum, Gnosis, Arbitrum, Base, ...), so
`GET /wallet/address/{seedAddress}/{slot?}` (above) - which derives both the AVM and EVM address for a
seed/slot in one call - takes no chain parameter for the EVM half at all.

`POST /wallet/{network}/{address}/sign` signs EVM transactions too (see "Wallet API" above) - build one
yourself (this API has no EVM transaction-*building* helper, unlike `AlgorandTransactionBuilder` for AVM) as
JSON matching `EvmTransactionRequest`: `{ "chainId", "nonce", "to", "value", "data", "gasLimit", "gasPrice" }`
for a legacy transaction, or the same with `"gasPrice"` replaced by `"maxFeePerGas"`+`"maxPriorityFeePerGas"`
for EIP-1559 - every numeric field is a decimal or `0x`-prefixed hex **string** (wei-scale values exceed a
safe JSON number), base64-encode the UTF-8 JSON, and pass it as one of `POST /wallet/{network}/{address}/sign`'s
`transactions` entries. The response's signed bytes (also base64) are ready to broadcast via that chain's own
`eth_sendRawTransaction` - this API does not broadcast for you (unlike Algorand/Bitcoin-family transactions,
which BiatecMCP's one `submitTransactionToBlockchain` tool submits, via the shared Algod connection or a public block
explorer respectively, depending on the `network` parameter). No spending-limit enforcement for
EVM yet (BiatecMCP's `getCryptoBalance` tool queries EVM balances directly against a public RPC, without ever
involving this API, since that needs no key material).

See [the supported-chains page](https://oidc.biatec.io/chains.html) for the full capability matrix, and this
repo's `CLAUDE.md` for the deeper architecture writeup (`IEvmChainRegistry`, `INetworkResolver`, etc., all in
`BiatecMCP` - BiatecOIDC itself has no EVM chain registry, since chain-specific RPC discovery is only needed
for balance queries).

## Important Claims in Tokens

ID token and access token contain these relevant claims:

- `sub`: pairwise subject per client and email.
- `email`: authenticated Google email.
- `name` and `preferred_username`: shortened identity (first 4 + last 4 chars of the primary seed address).
- `primary_seed_address`: the current primary seed's own identifying (Algorand slot-0) address — a *seed
  selector*, not a derived per-chain address. To get an actual signing address (Algorand or Ethereum-family)
  for a given slot, call `GET /wallet/address/{seedAddress}/{slot?}` (defaulting `seedAddress` to this claim
  if you don't already know which seed you want) — the same call for both chain families.
- Standard claims: `iss`, `aud`, `exp`, `iat`, `nbf`, `jti`.

Access tokens additionally carry, when applicable:

- `biatec_idp`: which provider (`Google`/`Microsoft`) the wallet is stored under.
- `sign`: `"true"` — only present if the `sign` scope was requested and is allowlisted for your client.
- `manage-limits`: `"true"` — only present if the `manage-limits` scope was requested and allowlisted.
- `rekey`: `"true"` — only present if the `rekey` scope was requested and allowlisted. The strictest wallet
  claim: without it, `POST /wallet/sign` refuses (403) any transaction group containing a rekey transaction,
  even with `sign` present.
- `provider_token`: an encrypted (never plaintext) copy of your Google/Microsoft access token, cached so wallet
  API calls don't need to separately supply one — only present when one was available to cache at issuance time.
  Opaque to you (and to any relying party inspecting the token) — only this service can decrypt it. See "Provider
  access token caching" below.

Important behavior for Drive consent:

- Login no longer fails when Google Drive access is denied.
- Tokens are still issued for `openid profile email` authentication.
- `primary_seed_address` is optional and omitted when Drive access is unavailable.
- Integrator apps should treat `primary_seed_address` as nullable and request incremental consent only when Drive-backed actions are needed.

## Scope handling

Every `/authorize` request is checked against two kinds of "unexpected scope", handled differently on purpose:

- **Scopes this server recognizes but this client isn't allowlisted for** — in practice, `sign`, `manage-limits`,
  and/or `rekey` requested by a client whose `AllowedScopes` doesn't include them (see "Client registration"
  below). This **fails the whole request** with `invalid_scope`; the error description names exactly which
  scope(s) aren't allowlisted, e.g. `"This client is not allowlisted for scope(s): manage-limits. Add them to
  this client's AllowedScopes in JwtIssuer:Clients to request them."`. This is deliberately loud - if you asked
  for `manage-limits` and expected to get it, silently issuing a token without it would just leave you confused
  later about why `PUT /wallet/limits` returns 403.
- **Scopes this server has never heard of at all** — a typo, or a scope your OIDC client library auto-appends
  whether you asked for it or not (e.g. some MSAL/Azure AD-flavored clients send a literal `.default` scope when
  none is explicitly configured). These are **silently dropped**, never rejected - there's nothing you could even
  fix here, and failing the whole login over library-injected noise would be worse than just ignoring it.

`openid` itself is the one scope that's always required regardless of allowlisting - omitting it fails with
`invalid_scope`, `"The openid scope is required."`. `profile` and `email` are always granted to every registered
client, regardless of its `AllowedScopes` list - only `sign`/`manage-limits` are actually gated by it.

The scope you actually end up with (after unrecognized scopes are dropped) is always visible in the token
response's `scope` field (and, for an authorization-code exchange, the same value the `/token` response returns)
- check that field, or `GET /userinfo`/the access token's own `sign`/`manage-limits` claims, if you're ever
unsure what a token was actually granted.

## Dynamic Client Registration and resource indicators (for MCP-class clients)

Most integrating relying parties are pre-registered by an operator adding a `JwtIssuerClientConfiguration` entry
under `JwtIssuer:Clients` in configuration - nothing in this section changes that path, and you can skip it
entirely if that's how you're integrating. This section is for clients that can't realistically be
pre-registered one at a time - the canonical example is an MCP (Model Context Protocol) client like Claude
Desktop, Claude.ai, or a VS Code extension, per the
[MCP Authorization spec](https://modelcontextprotocol.io/specification/draft/basic/authorization). BiatecMCP
(`https://mcp.biatec.io`) is this authorization server's first consumer of both mechanisms below.

### `POST /register` (RFC 7591 Dynamic Client Registration)

Registers a new **public** client at connect time - no client secret is ever issued by this endpoint, and
`token_endpoint_auth_method` must be `"none"` (or omitted) in the request, or it's rejected with
`invalid_client_metadata`.

Request:

```json
{
  "client_name": "My MCP Client",
  "redirect_uris": ["http://127.0.0.1:33445/callback"],
  "scope": "openid sign"
}
```

`redirect_uris` must be non-empty; each entry must be `https://` or a loopback `http://127.0.0.1`/
`http://localhost` URI (same policy `/authorize` itself applies via `AllowHttpForLoopbackRedirectUris`) - a
disallowed URI is rejected with `invalid_client_metadata`.

Response (`201 Created`):

```json
{
  "client_id": "…opaque, randomly generated…",
  "client_id_issued_at": 1735680000,
  "redirect_uris": ["http://127.0.0.1:33445/callback"],
  "token_endpoint_auth_method": "none",
  "grant_types": ["authorization_code", "refresh_token"],
  "response_types": ["code"],
  "scope": "openid profile email sign"
}
```

Whatever `scope` you requested, the client's actual `AllowedScopes` is always capped to
`JwtIssuer:DynamicClientRegistrationDefaultScopes` (default: `openid profile email sign`) - a
dynamically-registered client can **never** obtain `manage-limits` or `rekey`, the two highest-privilege wallet
scopes, this way. If your integration genuinely needs one of those, register a static `JwtIssuer:Clients` entry
with the same `client_id` this endpoint returned you - a static entry always takes precedence over a dynamic one
with the same id, so this is how an operator "upgrades" a self-registered client after the fact without you
needing to re-register.

Registered clients are stored in Redis with no expiry - there is currently no way to deregister one yourself.

### `resource` parameter (RFC 8707 resource indicators)

If you're integrating a resource server that many different clients (not all pre-known to you) need to obtain
tokens valid for, include a `resource` parameter - your resource server's own canonical URI (e.g.
`https://mcp.biatec.io/tools`) - on **both** `/authorize` and `/token`. It must be identical on both calls, and
must be one of the URIs an operator has added to `JwtIssuer:ProtectedResources` in configuration; an
unrecognized or omitted-on-one-side-only `resource` fails with `invalid_target`.

When a valid `resource` is present, the issued access token's `aud` claim contains **both** the requesting
`client_id` and the resource URI (a standard multi-value JWT `aud`). This is what lets your resource server
validate tokens from *any* client (including ones registered via `POST /register` above, whose `client_id` you
don't know in advance) against one fixed audience value, using ordinary local JWT validation against this
server's JWKS - no per-request call back to this server needed. If you never send `resource`, `aud` is exactly
`[client_id]`, unchanged from before this existed - this is purely additive and doesn't affect any existing
integration that doesn't opt in.

## Wallet API (`sign` / `manage-limits` / `rekey` scopes)

Beyond identity, Biatec OIDC can sign transaction groups directly on behalf of the wallet owner - both
Algorand-family (AVM) and Ethereum-family (EVM) chains, though only AVM signing is subject to the
daily/weekly/monthly spending limits the owner controls, in a currency of their choosing (EVM spending limits
aren't implemented yet). This is a separate, opt-in capability — request these scopes at `/authorize` only if
your integration needs them, and they must be explicitly added to your client's allowed scopes when it's
registered (never granted implicitly, regardless of what you request).

Full request/response examples, curl snippets, and a live discovery link are on the documentation site at
`https://oidc.biatec.io/` (the `#wallet-api` section) — this section is a summary.

None of these endpoints accept your Google/Microsoft access token as a parameter, ever - only the Biatec bearer
token in the `Authorization` header. The Google/Microsoft token needed behind the scenes to read/write your
self-custody data is resolved entirely from an encrypted copy cached inside that bearer token itself (see
"Provider access token caching" below) - this is what lets the exact same Biatec token work from any
device/backend, not just the one the user originally signed in on.

- **`POST /wallet/{network}/{address}/sign`** (needs `sign`) — body: `{ "transactions": [...] }`. `address`
  selects which identity signs (see "Address-centric wallet API" above for how it resolves to a seed/slot,
  and the migration table from the old body-field selector); unknown to Biatec fails with
  `400 address_not_active`. Returns `{ "signedTransactions": [...] }` in the same order as the request. Each
  `transactions`/`signedTransactions` entry's encoding, and what's checked before signing, depends on
  `network`'s chain family:
  - **AVM**: each entry is base64 msgpack. Additionally needs `rekey` if any transaction carries Algorand's
    `rekey` field - a `sign`-only token gets `403 insufficient_scope` naming `rekey`, and nothing in the
    group signs. A transaction whose own sender doesn't match `address` fails with `400 sender_mismatch`.
    **Only on Algorand mainnet** (`network` resolving to genesis id `mainnet-v1.0`) is every payment/
    asset-transfer in the group priced in USD via the Biatec Router, with the group's *total* checked
    against the signing identity's global **and** per-address spending limits *before* anything is
    signed — if the total would exceed either configured (non-zero) limit, the whole request is rejected
    (`403 spending_limit_exceeded`) and nothing is signed. A `503` (`asset_valuation_failed` or
    `spending_limit_currency_unavailable`) means a spent asset couldn't be priced, or the caller's limit
    currency's exchange rate couldn't be fetched — every transaction is subject to the limit on mainnet, so
    an unpriceable asset fails the request rather than being silently treated as free. **On every other AVM
    network** (testnet, Voi, Aramid, ...) the Biatec Router isn't deployed at all, so pricing/limit
    enforcement is skipped entirely — a testnet transfer signs unconditionally regardless of any limits
    configured for the account, since those limits are meaningless without mainnet's real USD pricing. This
    is not a bug to work around: if you need enforced spending limits, test against mainnet, or treat
    testnet transfers as inherently unlimited in your own integration.
  - **EVM**: each entry is base64-encoded UTF-8 JSON: `{ "chainId", "nonce", "to", "value", "data",
    "gasLimit", "gasPrice" }` for a legacy transaction, or the same with `gasPrice` replaced by
    `maxFeePerGas`+`maxPriorityFeePerGas` for EIP-1559 — every numeric field a decimal or `0x`-prefixed hex
    **string** (never a JSON number — wei-scale values exceed a JSON number's safe integer range). Exactly
    one of the two fee shapes must be given, or the request 400s `invalid_request`. No sender check (an
    unsigned EVM transaction carries no sender field at all — it's *derived* from whichever key signs it),
    no rekey concept, and **no spending-limit enforcement yet**. The response entry is the RLP-encoded
    signed transaction, base64-encoded — broadcast it yourself via that chain's own `eth_sendRawTransaction`
    (this endpoint does not broadcast).
- **`GET`/`PUT /wallet/limits`** (`GET` only needs to be authenticated; `PUT` needs `manage-limits`) — read/
  set the account-wide global spending-limit bucket. Shape:
  `{ "currencyCode": "USD", "dailyLimit": 100, "weeklyLimit": 500, "monthlyLimit": 2000, "address": null,
  "network": null, "seedAddress": null, "slot": 0 }` (`0` on any of the three limit fields means that
  window is unbounded). A bucket that's never been configured gets an all-zero, USD-denominated default
  rather than a 404. `currencyCode` defaults to `"USD"` if omitted/blank on `PUT`; an unsupported code is
  rejected with `400 unsupported_currency` (see `GET /wallet/limits/currencies` for the supported list).
- **`GET`/`PUT /wallet/{network}/{address}/limits`** — same shapes/claims as the global bucket above, but for
  the per-address bucket tied to `address` (resolved the same way as `POST /wallet/sign`'s `address`) - the
  response's `address`/`network`/`seedAddress`/`slot` fields are populated instead of `null`. The limits
  belong to the wallet owner, not to your application — they apply the same way across every app the owner
  has authorized with a `sign`-scoped token.
- **`GET /wallet/limits/currencies`** (only needs to be authenticated) — every currency `PUT /wallet/limits`
  will accept, with its current USD rate: `{ "currencies": [ { "code": "USD", "name": null, "usdPerUnit": 1.0 },
  { "code": "EUR", "name": "EMU euro", "usdPerUnit": 1.08 }, ... ] }`. Rates come from the Czech National Bank's
  daily fixing and are cached for several hours - not a real-time feed.
- All three spending-limit files — the settings above and a rolling ledger of every signed payment/asset-transfer
  (used to compute the real trailing spend without re-querying the blockchain on every sign) — are AES-encrypted
  and stored in the wallet owner's own Google Drive/OneDrive folder, exactly like the self-custody account file
  itself. Biatec's servers never persist this data in plaintext.
- The Google/Microsoft token resolved from your bearer token is used once, in-memory, per request, to read and
  decrypt the owner's self-custody file and spending-limit data, and is never persisted by these endpoints
  themselves — same self-custody model as the rest of this service (see the root `CLAUDE.md`). If no provider
  token was ever cached for this session (or it's since gone stale - see below), the call fails with
  `401 storage_access_denied`; there is no parameter to work around this with, the caller needs a fresh
  interactive sign-in through `/authorize`.

## Multi-address signing

Under the hood, every signing identity is still a `(seedAddress, slot)` pair: `seedAddress` selects
*which seed* (its own identifying slot-0 address, from `GET /wallet/seeds`), `slot`
selects the ARC-76 derivation index *within* that seed (default `0`). What changed is how you *address* one
at the API surface - see "Address-centric wallet API" above: instead of passing `seedAddress`/`slot`
directly to `POST /wallet/sign`/`PUT /wallet/limits`, you pass the resulting **address** in the route, and
Biatec resolves it back to the seed/slot that signs for it. This is addressable independently of which seed
is currently "primary" - you don't need to call `PUT /wallet/seeds/primary` to sign with a non-default
identity, just derive/activate the address you want and use it directly.

- **`GET /wallet/seeds`** (only needs to be authenticated) — lists every seed's identifying address and
  whether it's primary: `{ "seeds": [ { "address": "ABC...", "createdUtc": "...", "isPrimary": true }, ... ] }`.
- **`GET /wallet/address/{seedAddress}/{slot?}`** (only needs to be authenticated) — derives (without
  signing anything) the address at `slot` (default `0`) for the named seed, for every currently-supported
  chain family in one call: `{ "address": "derived-avm...", "evmAddress": "0xderived-evm...",
  "seedAddress": "ABC...", "slot": 3 }`. `400 seed_not_found` if `seedAddress` doesn't match any seed in
  the vault. Also registers both derived addresses for later `POST /wallet/{network}/{address}/sign` calls
  (see "Address-centric wallet API" above).
- Spending limits are two-tiered per the `GET`/`PUT /wallet/limits`/`GET`/`PUT /wallet/{network}/{address}/limits`
  bullets above: a **global** bucket that counts every signed transaction from any address together, and
  independent **per-address** buckets. A transaction signed with a given `(seedAddress, slot)` identity is
  checked against both - it's blocked if it would exceed either.

## Multi-seed vault and rekey

A wallet owner isn't limited to a single Algorand seed. Biatec stores a *vault* of independently-generated
seeds, one of which is "primary" - the one `POST /wallet/sign` and every other signing operation derives from.
Seeds are never deleted, even once superseded, since an older one may still authorize the account on a
different network, or be part of a multisig configured entirely outside Biatec.

- **`GET /wallet/seeds`** (only needs to be authenticated) — lists every seed ever generated, oldest first:
  `{ "seeds": [ { "address": "ABC...", "createdUtc": "2026-01-01T00:00:00Z", "isPrimary": true }, ... ] }`. The
  mnemonic itself is never returned by any endpoint - `address` (that seed's own slot-0 derived account) is
  how you refer to a specific seed elsewhere in this API.
- **`POST /wallet/seeds`** (needs `rekey`) — generates a fresh, independent seed and appends it to the vault.
  The new seed starts out **not** primary (unless it's the caller's very first seed ever) - minting a spare
  key never by itself changes what Biatec currently signs with. Returns the new seed's summary, same shape as
  a `GET /wallet/seeds` entry.
- **`PUT /wallet/seeds/primary`** (needs `sign`) — body: `{ "address": "ABC..." }`. Makes the named seed
  primary (demoting whichever one was primary before). `400 seed_not_found` if no seed in the vault has that
  address.

**The recovery-from-suspected-compromise flow**, end to end:
1. `POST /wallet/seeds` to mint a new seed - call it `newAddress`.
2. Your backend builds an Algorand transaction with `sender` = the account's existing address and
   `rekey`/`RekeyTo` = `newAddress`, and calls `POST /wallet/{network}/{existingAddress}/sign` with a token
   that has **both** `sign` and `rekey` (this is what actually gets checked - minting a spare seed via step 1
   requires only `rekey`, but the transaction that reassigns the account requires both, same as any other
   signed transaction).
3. Submit the signed transaction to the network yourself and wait for confirmation - Biatec never submits
   transactions on your behalf, only signs them.
4. Only once the rekey is confirmed on-chain, call `PUT /wallet/seeds/primary` with `newAddress`. Doing this
   *before* on-chain confirmation would make Biatec start signing with a key the account no longer recognizes
   - the account's original key remains the correct one to sign with until the rekey transaction actually
   lands.

The `rekey` scope is the single most dangerous one this service issues - see "Wallet API" above for why it's
gated separately from `sign`, and note the consent screen shows a distinct, explicit danger warning whenever a
client requests it.

## Cross-cloud vault backup

An explicit, user-triggered copy of the encrypted vault file from a user's primary cloud provider (whichever
one their Biatec session is currently using) to a *second* one they separately authorize - so losing access to
one cloud account (a provider ban, forgotten credentials) doesn't mean losing every key in the vault. Nothing
here runs automatically; it's a four-step flow, the middle two of which happen in a browser, not via the API:

1. **`POST /wallet/backup/start`** (needs `sign`) — body: `{ "targetProvider": "Microsoft" }` (must differ
   from the caller's current provider). Returns `{ "linkId": "...", "authorizeUrl": "https://.../wallet/backup/authorize?linkId=..." }`.
2. Open `authorizeUrl` in a browser. It redirects to the target provider's own consent screen (requesting only
   the storage-write scope needed to hold the vault file - nothing else).
3. After the user consents, the browser lands on **`GET /wallet/backup/callback`**, which exchanges the
   authorization code for the target provider's access token, verifies it actually grants storage-write
   access, and shows a plain confirmation page. This step is *not* the normal OIDC sign-in flow - it
   deliberately never touches the caller's `biatec_idp` cookie session, so authorizing a second provider here
   never changes which provider your Biatec token is tied to.
4. **`POST /wallet/backup/complete`** (needs `sign`) — body: `{ "linkId": "..." }`. Downloads the vault's
   current encrypted bytes from the primary provider and uploads the identical bytes to the target provider
   under the same file name (no re-encryption needed - both live under the same AES key ring regardless of
   storage backend). The target provider's access token is used exactly once, for this copy, and is never
   cached or persisted afterwards - a `400 backup_failed` means the link expired, was already used, or the
   copy itself failed; start again from step 1.

## Provider access token caching

The wallet API's whole point is that a relying-party (RP) backend talks to Biatec using only its Biatec-issued
`access_token` — it never sees, stores, or manages the user's actual Google/Microsoft OAuth token itself. That
means Biatec has to be able to get at that provider token on every `/wallet/sign`/`/wallet/limits` call using
*only* the Biatec bearer token the RP presents. This section explains how, and what that trades off.

**The mechanism.** At the moment a Biatec access/refresh token is minted — after the user completes an
interactive Google/Microsoft sign-in, while the ambient cookie session still has their live provider token — that
provider token is AES-256-GCM encrypted (`BusinessLogic/ProviderAccessTokenProtector.cs`, same authenticated
format `AesEncryptionHelper` uses for the self-custody file, but under a **separate, dedicated, independently
rotatable key ring** — `ProviderTokenProtection` in config, never `AesOptions`; see "Key rotation" below) and
embedded as a private claim
(`provider_token`) on the issued Biatec access token. `WalletController` decrypts that claim, in-memory, for the
duration of a single request, on every call - there is no way to supply your own Google/Microsoft token instead;
the Biatec bearer token is the only credential every wallet endpoint accepts. Nothing about this changes what an
RP integrating against this API needs to do: it already just forwards the Biatec `access_token` as a bearer token
everywhere; it never sees, stores, or forwards the user's actual Google/Microsoft token at all.

**Why not a server-side cache (e.g. Redis, keyed by the Biatec token) instead?** That was the alternative
considered here. Embedding the (encrypted) token *inside* the Biatec token the client already holds, rather than
in a lookup table on the server, means there is no new "list of every active user's provider token" for an
attacker to dump in one query if the database/cache is compromised — the ciphertext only exists inside tokens
already scattered across whichever RPs currently hold them, decryptable only with a key that (by design) never
leaves this service's own config/secret store.

**What this does and doesn't protect against.** Being explicit here matters more than usual, because this is
exactly the kind of feature that increases blast radius if Biatec's server is ever compromised — that's the
trade-off being made, deliberately, to support the "RP only ever holds a Biatec token" model:

- If an attacker compromises **only** `ProviderTokenProtection` (e.g. a narrow secret-store leak) without
  also compromising `AesOptions` (the self-custody file's key ring) or `JwtIssuer:SigningPrivateKeyPem`, they can
  decrypt any `provider_token` claim they can get their hands on, but still can't decrypt the self-custody account
  file itself or forge new Biatec tokens. This is why the keys are separate.
- If an attacker gets **full** server compromise (all of the above, plus Redis, plus the ability to intercept live
  traffic), caching or not caching this token changes little — they could already intercept/derive it from live
  requests, or decrypt the self-custody file directly with the leaked `AesOptions` key. The self-custody account
  file (not the provider token) is the actually valuable secret; the provider token by itself only grants
  `drive.file`/`Files.ReadWrite.AppFolder`-scoped access to a single app-created folder, not the account file's
  plaintext mnemonic.
- A **stolen Biatec access token** (e.g. an RP's own backend gets compromised, or a token leaks in a log) now also
  carries a live, usable Google/Microsoft token, for as long as both remain valid. This is no *new* exposure
  specific to caching, though — a stolen Biatec `sign`-scoped access token could already be replayed against
  `/wallet/sign` to move funds up to the spending limit regardless of whether a provider token happened to be
  attached. Treat a leaked access token as fully compromised either way — see "Security Recommendations" below on
  TLS and not logging tokens.
- The cached provider access token would otherwise expire on Google/Microsoft's own schedule (their access tokens
  typically last around an hour), independently of how long the Biatec access/refresh token chain carrying it
  stays alive (up to `RefreshTokenLifetimeDays`, 30 by default). To prevent that from surfacing as an avoidable
  `401 storage_access_denied`, the caller's provider **refresh** token is cached the same way, under a second
  private claim (`provider_refresh_token`, same key/format as `provider_token` - see
  `ProviderAccessTokenProtector.RefreshClaimType`), captured at the same moment. It's spent in two places:
  - **Every `grant_type=refresh_token` call** (`JwtIssuerService.RenewProviderTokenAsync`) - a server-to-server
    call with no ambient cookie session, so this is the only way to get a *fresher* provider access token onto the
    newly-issued Biatec access token rather than just carrying the old one forward unchanged. Google normally
    doesn't rotate the refresh token on renewal; Microsoft Entra ID always does - either way, whatever refresh
    token comes back (rotated or not) is what gets cached going forward.
  - **Opportunistically, inside `WalletController`**, if a wallet call fails with `UnauthorizedAccessException`
    (the cached provider access token went stale mid-lifetime of an otherwise still-valid Biatec token) - it
    renews once and retries the same call with the fresh token. This renewed token is used for that one request
    only; it can't be written back into the caller's already-issued, signed bearer token, so the next call still
    resolves the original cached access token until the client either renews its Biatec refresh token (the durable
    fix, above) or this happens again.
  - If there's no cached provider refresh token at all (a session that predates this feature) or the provider
    rejects it (revoked/expired), both paths fall back to the previous behavior unchanged: the caller needs a
    fresh interactive sign-in through `/authorize` to re-cache one. There is still no parameter on any wallet
    endpoint to work around this.
- The key ring is configured the same way as `AesOptions`/`JwtIssuer:SigningPrivateKeyPem` — via a Kubernetes
  Secret in production (see `k8s/stage/generate-stage-secret.sh` for how stage mints its own, separate copy),
  never committed as a real production secret. If `ProviderTokenProtection:ActiveKeyId` doesn't resolve to a
  valid key, no `provider_token` claim ever gets embedded (nothing throws at issuance time) - but every wallet
  endpoint would then have no way at all to resolve a provider token, so every call fails
  `401 storage_access_denied` until the key is configured. This key ring is required infrastructure for the
  wallet API to work at all, unlike before this feature existed.

## Configuration

Configure `JwtIssuer` in `appsettings.json`. Leave `Issuer` blank/unset to have it derived from
each incoming request's own scheme+host instead of a fixed value — this is what the production
deployment actually does, specifically so both `oidc.biatec.io` and the legacy `google.biatec.io`
alias each serve their own internally-consistent `iss`/discovery `issuer` without one breaking the
other. Only set `Issuer` explicitly (as below) if this service is reachable at exactly one host.

```json
"JwtIssuer": {
  "Enabled": true,
  "Issuer": "https://oidc.biatec.io",
  "KeyId": "biatec-main-key",
  "SigningPrivateKeyPem": "-----BEGIN PRIVATE KEY-----\\n...\\n-----END PRIVATE KEY-----",
  "AuthorizationCodeLifetimeSeconds": 120,
  "AccessTokenLifetimeMinutes": 15,
  "IdTokenLifetimeMinutes": 15,
  "RefreshTokenLifetimeDays": 30,
  "AllowHttpForLoopbackRedirectUris": true,
  "Clients": [
    {
      "ClientId": "my-app",
      "DisplayName": "My App",
      "ClientSecret": "super-strong-secret",
      "RedirectUris": [
        "https://*.example.com/auth/callback",
        "http://localhost:3000/auth/callback"
      ],
      "PostLogoutRedirectUris": [
        "https://*.example.com/login",
        "http://localhost:3000/login"
      ],
      "AllowedScopes": ["openid", "profile", "email"]
    },
    {
      "ClientId": "my-mobile-app",
      "ClientSecret": null,
      "RedirectUris": [
        "io.example.myapp:/oauth2redirect"
      ],
      "PostLogoutRedirectUris": [
        "io.example.myapp:/oauth2redirect"
      ],
      "AllowedScopes": ["openid", "profile", "email"]
    }
  ]
}
```

A client is a **public client** whenever `ClientSecret` is `null`/empty — this is the correct registration for
Android/iOS/desktop apps and browser SPAs, which cannot keep a secret confidential. PKCE (`code_challenge` /
`code_verifier`) is mandatory for such clients; see "PKCE for Public Clients (Mobile / Desktop Apps)" below.
`RedirectUris` accepts custom (non-`http`/`https`) URI schemes, e.g. `io.example.myapp:/oauth2redirect` for an
Android app-link/custom-scheme redirect — the same allowlist and wildcard rules apply, matched on scheme + host +
port + path.

`DisplayName` is optional but recommended — it's the human-friendly name shown to the user on the provider-picker
and consent screens (e.g. "My App") instead of the raw `ClientId` (e.g. "my-app-pkce"). Falls back to `ClientId`
when left blank, so existing clients that never set it keep working, just with a less polished label until it's
filled in.

To let a client use the wallet API, add `"sign"` and/or `"manage-limits"` to its `AllowedScopes` — e.g.
`["openid", "profile", "email", "sign", "manage-limits"]`. Neither is included by default; requesting one at
`/authorize` without it being allowlisted fails the whole request with `invalid_scope` (see "Scope handling"
below) — the error description names exactly which scope(s) to add.

Notes:

- `SigningPrivateKeyPem` must be an RSA private key in PEM format.
  - Supported PEM headers: `BEGIN PRIVATE KEY` (PKCS#8) and `BEGIN RSA PRIVATE KEY` (PKCS#1)
  - Unsupported format: `BEGIN OPENSSH PRIVATE KEY` (common output of `ssh-keygen`)
- If you provide a file path in `SigningPrivateKeyPem`, the service will read PEM content from that file.
- If `SigningPrivateKeyPem` is empty, service falls back to ephemeral key (not for production).
- Redirect URIs are allowlisted and support `*` wildcards in the configured host, path, and query.
  - Example: `https://*.example.com/auth/callback` matches `https://tenant-a.example.com/auth/callback`.
  - `https://*.example.com/auth/callback` does not match `https://example.com/auth/callback`; register the root domain separately when needed.
  - Scheme and port must still match exactly.
- Post-logout redirect URIs are allowlisted via `PostLogoutRedirectUris` with the same wildcard rules.
  - If `PostLogoutRedirectUris` is empty for a client, `RedirectUris` are used as fallback allowlist for logout redirects.

Also configure `ProviderTokenProtection` — the dedicated key ring that encrypts the cached Google/Microsoft
access/refresh tokens embedded in issued access tokens (see "Provider access token caching" above). **Required**
for the wallet API to work at all (no wallet endpoint accepts a caller-supplied provider token as a fallback) and
**deliberately a separate key ring from `AesOptions`**, so the two secrets can be rotated independently:

```json
"ProviderTokenProtection": {
  "ActiveKeyId": "2026-08",
  "Keys": [
    {
      "KeyId": "2026-08",
      "Key": "<base64, 32 random bytes>",
      "IV": "<base64, 16 random bytes>"
    }
  ]
}
```

```bash
openssl rand -base64 32   # Key
openssl rand -base64 16   # IV
```

If `ActiveKeyId` is unset (or doesn't resolve to a valid entry in `Keys`) outside `Development`, the service
refuses to start (same fail-fast precedent as `JwtIssuer:SigningPrivateKeyPem`) rather than silently leaving
every wallet call failing with an unexplained 401. See "Key rotation" below for how to rotate this (and
`AesOptions`) without invalidating anything already cached.

## Key rotation

Both `AesOptions` and `ProviderTokenProtection` are rotatable key rings
(`BiatecSelfCustodyCore.Model.IAesKeyRingConfiguration`), not a single `{Key, IV}` pair: an `ActiveKeyId` names
which `Keys[]` entry is used for all *new* encryption, and every other entry is kept only so data encrypted
under it can still be decrypted (and then migrated onto the active key - see below). This replaces the previous
design, where changing a key's value silently orphaned all existing data encrypted under the old one - for the
self-custody account file specifically, that meant a rotated key could silently create a **brand-new random
account** the next time a user signed in, since the file the old key encrypted could no longer be found under
the new key's name.

**To rotate:**

1. Generate a new key/IV pair (`openssl rand -base64 32`/`16`) and pick a new, never-reused `KeyId` (a date like
   `"2027-02"` works well).
2. Add it as a **new** entry in `Keys[]` (don't remove the old one yet) and set `ActiveKeyId` to the new
   `KeyId`. Update the Kubernetes Secret with both the new generation and every existing one still present
   (`kubectl get secret <name> -o json` shows the currently-deployed literals if you need to read them back)
   using the standard array env-var convention: `AesOptions__Keys__0__KeyId`, `AesOptions__Keys__0__Key`,
   `AesOptions__Keys__0__IV`, `AesOptions__Keys__1__KeyId`, ... (see
   `k8s/stage/generate-stage-secret.sh`'s rotation comment for a worked example).
3. `kubectl rollout restart deployment/...` for every affected deployment. These keys arrive as plain
   environment variables (`envFrom: secretRef`), which `IOptionsMonitor<T>` cannot hot-reload, so a restart is
   required to pick up the change - being a rolling restart across replicas, it's already zero-downtime.
4. From that point on:
   - **New encryption** (new self-custody accounts, newly-issued Biatec tokens' cached provider tokens, updated
     spending limits) uses the new active key immediately.
   - **Existing data under the old key** keeps decrypting correctly, and - for the self-custody account file and
     `SpendingLimitService`'s limits/ledger files specifically - is automatically **re-encrypted under the new
     active key the next time it's read** (`EncryptedKeyRingFileStore`, `BiatecSelfCustodyCore/Helper/`): each
     key generation's file lives under a distinct name derived from a hash of that generation's key
     (`AesEncryptionHelper.MakeAesId`, the `%AESID%` placeholder in `App:StorageFileName`/the spending-limit
     file name templates), so a load that doesn't find the file under the active generation's name falls back
     through historical generations, and the moment it finds one, re-encrypts + re-uploads it under the active
     name and deletes the stale file. No batch migration job is needed - it happens lazily, "as soon as
     possible," the next time each user's data is touched.
   - **Cached provider tokens** (`provider_token`/`provider_refresh_token` claims, `ProviderTokenProtection`)
     have no file to migrate - they live inside already-issued Biatec tokens the wallet API doesn't control.
     `ProviderAccessTokenProtector.Unprotect` tries the active key then every historical key in turn (safe,
     because it only ever writes the authenticated AES-GCM format, so a wrong key deterministically fails the
     auth-tag check), and every *newly issued or refreshed* Biatec token naturally picks up the new active key
     via `JwtIssuerService.CreateAccessToken`/`RenewProviderTokenAsync` - so these migrate onto the new key
     simply by tokens being refreshed in the normal course of use.
5. `EncryptedKeyRingFileStore` logs an `Information`-level message ("Migrating ... from AES key generation ...
   to the active generation ...") every time it migrates a file off a historical key - once you stop seeing a
   given `KeyId` mentioned across a full refresh-token lifetime (`JwtIssuer:RefreshTokenLifetimeDays`) or longer,
   it's reasonably safe to drop that generation from `Keys[]` entirely. Removing a generation still in active
   use means any data/token that hasn't yet been touched since the rotation becomes undecryptable, so err on
   the side of keeping old generations around longer than you think you need to.

### Generate compatible signing key (recommended)

Use OpenSSL to generate a PEM key the service can import:

```bash
openssl genpkey -algorithm RSA -pkeyopt rsa_keygen_bits:4096 -out jwt-signing-private.pem
openssl rsa -pubout -in jwt-signing-private.pem -out jwt-signing-public.pem
```

Then configure either:

1. Inline PEM with escaped newlines (`\\n`) in `SigningPrivateKeyPem`
2. A file path to `jwt-signing-private.pem` in `SigningPrivateKeyPem`

### Ed25519 / EdDSA note

`Ed25519` (`EdDSA`) is not currently wired in this service. The current JWT token stack used by this project is configured for `RS256` issuance and validation.

## Recommended Flow for Destination Project

Use authorization code flow.

1. Redirect browser to:

```text
GET https://oidc.biatec.io/authorize
  ?client_id=my-app
  &redirect_uri=https%3A%2F%2Fmy-app.example.com%2Fauth%2Fcallback
  &response_type=code
  &scope=openid%20profile%20email
  &state=<csrf_random>
  &nonce=<nonce_random>
```

2. User authenticates with Google at Biatec.
3. Biatec redirects back to your `redirect_uri` with `code` and `state`.
4. Your backend exchanges code at token endpoint:

```text
POST https://oidc.biatec.io/token
Content-Type: application/x-www-form-urlencoded
Authorization: Basic base64(client_id:client_secret)

grant_type=authorization_code
&code=<code>
&redirect_uri=https%3A%2F%2Fmy-app.example.com%2Fauth%2Fcallback
```

5. Validate ID token with:
   - issuer from discovery
   - jwks from `jwks_uri`
   - audience = your client id
   - expiration and signature
6. Use `refresh_token` at `/token` with `grant_type=refresh_token` when renewing.

## PKCE for Public Clients (Mobile / Desktop Apps)

Native apps (Android, iOS, desktop) and browser SPAs cannot store a `client_secret` confidentially — anyone can
decompile the app or read the bundle. Register these as a **public client** (`ClientSecret: null` in
`JwtIssuer:Clients`) and use PKCE (RFC 7636) instead of a client secret to protect the authorization code exchange.
The server rejects `response_type=code` authorization requests from a public client if `code_challenge` is missing.

This is the standard flow for an Android app using AppAuth (or an equivalent PKCE-capable OIDC library):

1. Generate a random `code_verifier` (43–128 chars, unreserved charset `[A-Za-z0-9-._~]`) and derive
   `code_challenge = BASE64URL(SHA256(code_verifier))` (`code_challenge_method=S256`; use `S256`, not `plain`, in
   production — `plain` exists only for clients that cannot compute SHA-256).
2. Open the system browser / Custom Tab to:

```text
GET https://oidc.biatec.io/authorize
  ?client_id=my-mobile-app
  &redirect_uri=io.example.myapp%3A%2Foauth2redirect
  &response_type=code
  &scope=openid%20profile%20email
  &state=<csrf_random>
  &nonce=<nonce_random>
  &code_challenge=<code_challenge>
  &code_challenge_method=S256
```

3. The user authenticates with Google at Biatec (same flow as the web case — the app never sees Google
   credentials).
4. Biatec redirects to the app's registered custom-scheme `redirect_uri` with `code` and `state`. The OS routes
   this back into the app (Android App Links / custom scheme intent filter).
5. The app exchanges the code directly from the device — no `client_secret`, no backend needed:

```text
POST https://oidc.biatec.io/token
Content-Type: application/x-www-form-urlencoded

grant_type=authorization_code
&code=<code>
&redirect_uri=io.example.myapp%3A%2Foauth2redirect
&client_id=my-mobile-app
&code_verifier=<code_verifier>
```

6. Validate the returned `id_token` the same way as the confidential-client flow (issuer, `jwks_uri`, audience,
   expiration, signature) and store `access_token`/`refresh_token` in platform-appropriate secure storage
   (Android Keystore-backed `EncryptedSharedPreferences`, iOS Keychain, etc.) — never in plain files or logs.
7. Refresh with `grant_type=refresh_token` at `/token` exactly like a confidential client; refresh does not require
   `code_verifier` (PKCE only protects the authorization code step).

Notes:

- `code_challenge_method` accepts `S256` (recommended) or `plain`.
- PKCE is optional (but still accepted and validated if sent) for confidential clients that have a `ClientSecret`
  configured — it does not replace the secret for those clients, it complements it.
- The token endpoint remains public/anonymous (`token_endpoint_auth_methods_supported` includes `none`) precisely
  to support this public-client, secret-less exchange; PKCE is what prevents a stolen authorization code from being
  redeemed by an attacker.

## RP-Initiated Logout Flow (Required for full sign-out)

Use standards-based RP-initiated logout so the Biatec IdP session is cleared, not just the local app session.

Dedicated requirements doc for Capitalism integrators:
- `BIATEC_OIDC_LOGOUT_REQUIREMENTS.md`

1. Clear your application session.
2. Redirect browser to:

```text
GET https://oidc.biatec.io/connect/endsession
  ?id_token_hint=<last_id_token>
  &post_logout_redirect_uri=https%3A%2F%2Fmy-app.example.com%2Flogin
  &state=<csrf_or_logout_state>
  &client_id=my-app
```

3. Biatec invalidates its authentication session cookie.
4. Browser is redirected to `post_logout_redirect_uri`.
5. `state` is preserved and returned as query parameter when provided.

Notes:

- `post_logout_redirect_uri` must be absolute and allowlisted for the client.
- Allowlist matching is based on scheme + host + port + path, with optional `*` wildcards in configured entries.
- Query parameters are allowed on top of an allowlisted base URI. Wildcards in `PostLogoutRedirectUris` are evaluated before query parameters are appended.
- `https://*.example.com/login` matches `https://tenant-a.example.com/login` but not `https://example.com/login`.
- For best interoperability, send both `id_token_hint` and `client_id`.
- Discovery metadata includes `end_session_endpoint` for dynamic client configuration.
- Capitalism frontend environment variable:
  - `VITE_BIATEC_OIDC_END_SESSION_URL=https://oidc.biatec.io/connect/endsession`

Example accepted logout redirect:

```text
Allowlisted base URI: http://localhost:5173/login
Runtime URI:          http://localhost:5173/login?redirect=%2F&oidc_retry=consent
```

This runtime URI is valid because it matches the allowlisted origin and path.

## Legacy Direct Token POST Flow

If needed, a direct token return is available:

```text
GET /authorize?returnUrl=https%3A%2F%2Fmy-app.example.com%2Fauth%2Fcallback
```

This path defaults to `response_type=id_token` and `response_mode=form_post` for compatibility.

## Security Recommendations

- Use HTTPS in production for all endpoints and redirect URIs.
- Keep authorization codes short lived.
- Always validate `state` for CSRF protection.
- Use strong client secrets for confidential clients.
- Register mobile/desktop/SPA clients as public clients (no `ClientSecret`) and require PKCE — never embed a
  `client_secret` in an app binary or frontend bundle.
- Rotate signing keys using `kid` changes and serve both old and new public keys during transition.
- Validate `iss`, `aud`, and signature on every token.

## Copilot Prompt for Destination Project

Use this prompt in your destination project so Copilot can scaffold integration quickly:

```text
Implement OpenID Connect authorization code login against issuer https://oidc.biatec.io.
Requirements:
- Discover metadata from /.well-known/openid-configuration.
- Start login by redirecting to /authorize with client_id, redirect_uri, response_type=code, scope=openid profile email, state, nonce.
- Handle callback, validate state, exchange code at /token with client_secret_basic.
- Validate id_token using jwks from jwks_uri (RS256, kid aware).
- Map claims: email, preferred_username, primary_seed_address.
- Store refresh_token securely and implement token refresh with grant_type=refresh_token.
- Add middleware/guard to reject invalid issuer, audience, signature, and expired tokens.
- Add unit tests for callback state validation and id_token signature validation.
```

## Validation Checklist

- Discovery document resolves and contains issuer, authorize, token, jwks endpoints.
- Redirect URI is allowlisted exactly.
- `/authorize` triggers Google login if no session cookie exists.
- `/token` returns access token, id token, refresh token for valid code.
- `/userinfo` returns expected claims for valid access token.
- `/introspect` returns `active=true` for valid access token.
- Discovery contains `end_session_endpoint`.
- Logout via `/connect/endsession` redirects to allowlisted `post_logout_redirect_uri`.
- A new login after logout requires a fresh Biatec authentication session.
- For a public (PKCE) client: `/authorize` without `code_challenge` returns `invalid_request`.
- For a public (PKCE) client: `/token` with a correct `code` but missing/wrong `code_verifier` returns
  `invalid_grant`; the matching `code_verifier` succeeds without any `client_secret`.
