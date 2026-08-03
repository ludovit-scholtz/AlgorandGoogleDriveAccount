# Biatec MCP Server

A Model Context Protocol (MCP) server that lets AI assistants (Claude Desktop, Claude.ai, VS Code, ChatGPT
connectors, etc.) read an Algorand address and sign/broadcast Algorand transactions on behalf of a self-custody
Biatec account — without this server ever holding any key material.

## Architecture: pure OAuth 2.1 resource server

BiatecMCP holds **no key material, no Google/Microsoft credentials, and no session state of its own**. It
delegates all authentication and signing to [BiatecOIDC](https://oidc.biatec.io), following the
[MCP Authorization specification](https://modelcontextprotocol.io/specification/draft/basic/authorization) (OAuth
2.1, RFC 9728 Protected Resource Metadata, RFC 8707 resource indicators):

1. An unauthenticated request to `POST /mcp` gets a `401` with
   `WWW-Authenticate: Bearer resource_metadata="https://mcp.biatec.io/.well-known/oauth-protected-resource"`.
2. The MCP client fetches that document, discovering `https://oidc.biatec.io` as the authorization server.
3. Since most MCP clients have no pre-arranged relationship with this server, the client self-registers via
   BiatecOIDC's `POST /register` (RFC 7591 Dynamic Client Registration — always a public client, never issued a
   secret).
4. The client completes the standard `/authorize` (PKCE required) + `/token` flow against BiatecOIDC, requesting
   the `resource=https://mcp.biatec.io/mcp` parameter (RFC 8707) alongside the `openid sign` scopes advertised in
   the Protected Resource Metadata document. The user signs in (Google or Microsoft, whichever their Biatec
   account uses) and consents on BiatecOIDC's own consent screen.
5. The resulting access token is presented to BiatecMCP as `Authorization: Bearer <token>`. BiatecMCP validates it
   **locally** (JWKS fetched from BiatecOIDC, no per-request network call) against its own resource URI.
6. Each tool call forwards that *same* bearer token to BiatecOIDC's wallet REST API
   (`POST /wallet/sign`/`GET /wallet/seeds`) — BiatecOIDC does the actual signing, and enforces the caller's
   spending limit and the `rekey` claim, on the caller's behalf.

See the repo root [CLAUDE.md](../CLAUDE.md)'s "MCP server" architecture note for the full code-level walkthrough,
and [BiatecOIDC/OIDC_INTEGRATION_GUIDE.md](../BiatecOIDC/OIDC_INTEGRATION_GUIDE.md) for the Dynamic Client
Registration / resource-indicator contract in detail.

## Multi-chain support

Every tool's `genesisId` parameter isn't limited to a small hardcoded list — it accepts any Algorand-family
chain published in [scholtz.github.io/AlgorandPublicData's genesis list](https://scholtz.github.io/AlgorandPublicData/genesis/genesis-list.json)
that currently has at least one publicly reachable algod node reporting the matching genesis hash (checked
live against each chain's [`public-algod-providers.json`](https://scholtz.github.io/AlgorandPublicData/algod/mainnet-v1.0/public-algod-providers.json),
via that node's own `/v2/transactions/params`). A locally-configured `Algod:Networks` entry always takes
precedence for a given `genesisId` (so an operator can still pin a specific node/explorer link); anything else
falls back to this dynamically discovered, liveness-verified registry, cached in-process for ~10 minutes. The
same registry backs `createBridgeTransaction`'s destination-liquidity check (below) and BiatecOIDC's public
`GET /chains` endpoint.

## EVM (Ethereum-family) support

Every Biatec account already has an Ethereum-family identity too, derived from the exact same seed as its
Algorand identity — no separate sign-in or consent needed. `getCryptoAddress`/`getCryptoBalance` work across
both chain families (Algorand-family and Ethereum-family) via one `network` parameter — pass `"Algorand"`,
`"Voi"`, `"Aramid"`, `"Ethereum"`, `"Gnosis"`, `"Arbitrum"`, `"Base"`, or any other public chain name/id; call
`listSupportedNetworks` to see what's currently resolvable. EVM chain RPCs are discovered from
[chainid.network's public chain list](https://chainid.network/chains.json), verified live only for the
specific chain asked about (not the whole ~2,700-chain list). **EVM support today covers address derivation
and native-token balance queries only** — sending/signing EVM transactions, ERC-20 token balances, spending
limits, and rekey are not yet available on EVM chains (rekey has no EVM equivalent at all). See
[BiatecOIDC's supported-chains page](https://oidc.biatec.io/chains.html) for the full per-chain-family
capability matrix.

## Available MCP tools

Wallet operations are three separate, chainable steps — **build** an unsigned transaction, **sign** it, then
**execute** (broadcast) it — rather than one monolithic call. Each `create*` tool only builds and never touches
BiatecOIDC or the network; only `signTransaction` and `executeAlgorandTransaction` require the `sign` scope.
Every tool's own description tells the connected AI assistant which tool to call next, so a plain "pay X"
request is handled as three chained tool calls automatically.

- **`getAlgorandAddress`** — returns an Algorand address for the signed-in account. With no arguments, returns the
  default identity (from the bearer token's own `algorand_address` claim, falling back to the primary seed from
  `GET /wallet/seeds`). Pass `slot` (ARC-76 derivation index — `1` for the "second address", `2` for the "third",
  etc.) and/or `primaryAddress` (a specific seed's identifying address, from `listAlgorandAddresses`) to derive a
  different address instead.
- **`listAlgorandAddresses`** — lists every seed's identifying address in the account, and which one is primary.
  Use an address from here as `primaryAddress` on the other tools.
- **`listSupportedNetworks`** — lists every blockchain network currently usable with `getCryptoAddress`/
  `getCryptoBalance`: every live Algorand-family chain, plus a few well-known Ethereum-family chains
  (Ethereum, Gnosis, Arbitrum, Base). Other public EVM chains not listed here also work by name or numeric
  chain id. No authentication required.
- **`getCryptoAddress`** — returns the signed-in account's address on a given `network` (Algorand-family
  *or* Ethereum-family — both derived from the same seed). An Algorand-family address is the same across
  every AVM chain; an Ethereum-family address is the same across every EVM chain — `network` just picks
  which family's address to return.
- **`getCryptoBalance`** — returns the native-currency balance (and, on Algorand-family chains, ASA
  holdings) for an address on a given `network`. Omit `address` to check the signed-in account's own
  address. EVM balances are the native gas token only (e.g. ETH) — ERC-20 token balances aren't supported.
- **`createPaymentTransaction`** — builds an unsigned native-ALGO payment or ASA transfer. An empty
  `receiverAccount` builds a self-transfer. Does not sign or broadcast.
- **`createOptInTransaction`** — builds an unsigned ASA opt-in (a zero-amount self-transfer). Does not sign or
  broadcast.
- **`createAssetCreateTransaction`** — builds an unsigned ASA (Algorand Standard Asset) creation transaction.
  `manager`/`reserve`/`freeze`/`clawback` each default to the creator's own address if not given.
- **`createSwapTransaction`** — quotes a swap across Biatec Router, Folks Router, and Haystack Router and reports
  the best price. Only builds a real unsigned transaction for Biatec Router's own route today — if a competing
  aggregator quotes better, the comparison is still returned but no transaction is attached for that route yet
  (see the note below).
- **`getBridgeConfiguration`** — fetches Aramid Finance's live bridge configuration (same on-chain + IPFS
  discovery `createBridgeTransaction` uses) and returns every chain Aramid knows about plus every route out of
  Algorand mainnet — amount bounds and fee schedule generations, token decimals — so an agent can confirm a
  `destinationNetwork`/`assetId`/`destinationToken` combination is actually valid before calling
  `createBridgeTransaction`, instead of guessing and getting a `RouteNotFound`/`AmountOutOfRange` error. No
  authentication required.
- **`createBridgeTransaction`** — builds an unsigned [Aramid Finance](https://aramid.finance) bridge transaction:
  a pay/axfer sent to Aramid's bridge deposit address (fetched live from Aramid's own on-chain + IPFS-hosted
  configuration — never hardcoded) with a note field encoding the destination chain/address/amounts per Aramid's
  `aramid-transfer/v1:j` format. Validates the route and Aramid's configured min/max amount bounds before
  building. For an Algorand-family destination chain with a currently-live public algod node (see "Multi-chain
  support" below), also verifies the bridge deposit address there actually holds enough of the destination token
  — and refuses to build the transaction (`InsufficientDestinationLiquidity`) if not, rather than returning
  something that would strand the transfer. For any other destination (EVM/NEAR chains, or an Algorand-family
  chain with no currently-live node), the response's `LiquidityVerified`/`Warning` fields explain why it
  couldn't be checked, and you should confirm independently before bridging anything but a small amount. Only
  bridging *from* Algorand mainnet is supported today.
- **`createMultisigTransaction`** — builds an unsigned payment/ASA transfer proposal from a `(version, threshold,
  participantAddresses)` multisig account. Each participant independently signs the returned envelope with their
  own `signTransaction` call (in their own wallet/MCP session — not necessarily this one), then the signed copies
  are combined with `mergeMultisigTransactions`.
- **`signTransaction`** — signs one or more unsigned transactions (from any `create*` tool, or a
  `createMultisigTransaction` envelope) via BiatecOIDC's `POST /wallet/sign`. Requires the `sign` scope; signs
  with the default identity unless `primaryAddress`/`slot` are given.
- **`mergeMultisigTransactions`** — combines independently-signed copies of the same multisig envelope (collected
  from each cosigner's own `signTransaction` call) into one transaction, once at least `threshold` signatures are
  present.
- **`executeAlgorandTransaction`** — broadcasts one or more already-signed transactions (base64 msgpack) to the
  network. Requires the `sign` scope.

### Example prompts

- *"what is my algorand address"* → `getAlgorandAddress()`
- *"what is my second address"* → `getAlgorandAddress(slot=1)`
- *"list all my algorand addresses"* → `listAlgorandAddresses()`
- *"what networks can I use"* → `listSupportedNetworks()`
- *"what is my ethereum address"* → `getCryptoAddress(network="Ethereum")`
- *"what is my address on Voi"* → `getCryptoAddress(network="Voi")` — same as `getAlgorandAddress()`, since an
  Algorand-family address is the same across every AVM chain.
- *"how much ETH do I have"* → `getCryptoBalance(network="Ethereum")`
- *"check the balance of 0xABCD...WXYZ on Arbitrum"* → `getCryptoBalance(network="Arbitrum", address="0xABCD...WXYZ")`
- *"pay to address ABCD...WXYZ 1 algo with note biatec"* → `createPaymentTransaction(receiverAccount="ABCD...WXYZ",
  amount=1000000, note="biatec")` → `signTransaction(...)` → `executeAlgorandTransaction(...)` — three chained
  calls, signing with the default identity (primary seed, slot 0).
- *"pay to address ABCD...WXYZ 1 algo with note biatec with my arc76 address SEED2...ADDR and slot 10"* → same
  chain, with `primaryAddress="SEED2...ADDR", slot=10` passed to both `createPaymentTransaction` and
  `signTransaction`.
- *"do self transfer with 1 algo amount and note field biatecmcp"* → `createPaymentTransaction(amount=1000000,
  note="biatecmcp")` (empty `receiverAccount` self-transfers) → `signTransaction` → `executeAlgorandTransaction`.
- *"opt in to asset 31566704"* → `createOptInTransaction(assetId=31566704)` → `signTransaction` →
  `executeAlgorandTransaction`.
- *"swap 1 algo for USDC"* → `createSwapTransaction(fromAssetId=0, toAssetId=31566704, amount=1000000)` — quotes
  all three aggregators; if Biatec Router wins, chain `signTransaction` → `executeAlgorandTransaction` on the
  returned transaction(s), otherwise the response explains which aggregator quoted better and that its
  transaction can't be built yet.
- *"propose a 2-of-3 multisig payment of 5 algo to address ABCD...WXYZ between my address, SEED2...ADDR, and
  SEED3...ADDR"* → `createMultisigTransaction(version=1, threshold=2, participantAddresses=[...], ...)`, then
  each participant runs `signTransaction` on the returned envelope in their own session, and any one party runs
  `mergeMultisigTransactions` on the collected signed copies followed by `executeAlgorandTransaction`.
- *"what bridge routes are available from Algorand mainnet"* → `getBridgeConfiguration()`, or
  `getBridgeConfiguration(destinationChainId=416101)` to filter to a specific destination (e.g. Voi).
- *"bridge 1 algo to my address VOI...ADDR on Voi"* → `createBridgeTransaction(assetId=0, amount=1000000,
  destinationNetwork=416101, destinationAddress="VOI...ADDR", destinationToken="<Voi ALGO token id>")` → review
  the returned fee/amount breakdown and `LiquidityVerified`/`Warning` fields → `signTransaction` →
  `executeAlgorandTransaction`.

Spending limits (`PUT /wallet/limits` on BiatecOIDC) can be configured globally (apply to every address) and/or
per address (`?primaryAddress=...&slot=...`) — a transaction is blocked if it would exceed either, enforced by
`signTransaction`'s underlying `POST /wallet/sign` call. See
[BiatecOIDC/OIDC_INTEGRATION_GUIDE.md](../BiatecOIDC/OIDC_INTEGRATION_GUIDE.md) for the wallet API's full
multi-address/spending-limit contract.

## Connecting an MCP client

Point your MCP client at:

```
https://mcp.biatec.io/mcp
```

(stage: `https://stage.mcp.biatec.io/mcp`). Any client that implements the MCP Authorization spec's OAuth
discovery flow (Claude Desktop, ChatGPT, VS Code, LM Studio, etc.) will handle steps 1–5 above automatically
the first time it connects — there is no manual "pairing" step to complete separately, and no session ID to
configure, no `mcp-remote` proxy, no local Node.js install. If your client supports a raw MCP server URL
field, that is all you need to enter. The full interactive setup walkthrough (with copy-paste config) for each
of the clients below is also on [the documentation site](https://mcp.biatec.io/#setup).

### Visual Studio Code

Add to `.vscode/mcp.json` (confirmed working):

```json
{
  "servers": {
    "Biatec": {
      "url": "https://mcp.biatec.io/mcp",
      "type": "http"
    }
  }
}
```

### Claude Desktop

Add to `claude_desktop_config.json` (`%APPDATA%\Claude\claude_desktop_config.json` on Windows,
`~/Library/Application Support/Claude/claude_desktop_config.json` on macOS — or `Settings → Connectors → Add
custom connector` in the UI instead of editing the file directly):

```json
{
  "mcpServers": {
    "Biatec": {
      "url": "https://mcp.biatec.io/mcp"
    }
  }
}
```

### ChatGPT Desktop

Requires a Business/Enterprise/Edu account with Developer Mode. `Settings → Connectors → toggle Developer
Mode → Create`, then enter `https://mcp.biatec.io/mcp` as the MCP Server URL, authentication `OAuth`, and
check "I trust this application".

### LM Studio

`Program` tab (right sidebar) → `Install` → `Edit mcp.json`:

```json
{
  "mcpServers": {
    "Biatec": {
      "url": "https://mcp.biatec.io/mcp"
    }
  }
}
```

## Local development

```bash
dotnet run --project BiatecMCP/BiatecMCP.csproj
```

`BiatecMCP/appsettings.json` points `Oidc:Issuer` at the live `https://oidc.biatec.io` and
`Mcp:CanonicalResourceUri` at `http://localhost:5110/mcp` by default, so a local run authenticates against the
real BiatecOIDC. No Redis, no Google/Microsoft OAuth credentials, and no `BiatecSelfCustodyCore` reference are
needed to run this project — everything self-custody-related lives in `BiatecOIDC`.

### Algod configuration

`Algod:Networks` in `appsettings.json` maps a `genesisId` (e.g. `mainnet-v1.0`, `testnet-v1.0`) to an Algod node
address/token and a block-explorer base URL for the `transferAsset`/`optIn` tools' `ExplorerLink` response field.

## Project structure

```
BiatecMCP/
├── Program.cs                     # OAuth 2.1 resource-server wiring (AddJwtBearer + AddMcp)
├── MCP/
│   └── BiatecMCP.cs                # The 3 MCP tools
├── BusinessLogic/
│   ├── IBiatecWalletClient.cs      # BiatecOIDC wallet API client (interface)
│   ├── BiatecWalletClient.cs       # ...(implementation, typed HttpClient)
│   └── WalletApiException.cs       # Carries BiatecOIDC's ProblemDetails back to a tool
├── Helper/
│   └── AlgorandTransactionBuilder.cs  # Unsigned transaction construction (no key material)
├── Model/
│   ├── Configuration.cs            # App:Host
│   ├── OidcConfiguration.cs        # Oidc:Issuer
│   ├── McpResourceConfiguration.cs # Mcp:CanonicalResourceUri
│   ├── AlgodConfiguration.cs
│   ├── CorsConfiguration.cs
│   └── WalletApiModels.cs          # DTOs mirroring BiatecOIDC's wallet API (duplicated, not shared)
└── wwwroot/
    ├── index.html
    ├── privacy.html
    └── terms.html
```

Every self-custody primitive (key storage, encryption, spending limits, signing) lives in `BiatecOIDC` and the
shared `BiatecSelfCustodyCore/` library it depends on — this project has no reference to either.

## Deployment

Production: `https://mcp.biatec.io` (`https://google.biatec.io` also still resolves here as a legacy alias).
Stage: `https://stage.mcp.biatec.io` (auto-deployed on every push to `master` via
`.github/workflows/deploy-stage.yml`; production requires the manually-triggered
`.github/workflows/promote-production.yml`). See the repo root [CLAUDE.md](../CLAUDE.md) for the full CI/CD and
Kubernetes ingress picture.

```bash
docker build -t biatec-mcp-server .
docker run -p 8080:8080 biatec-mcp-server
```

### Environment variables (production)

- `ASPNETCORE_ENVIRONMENT`: `Development`/`Production`
- `Oidc__Issuer`: which BiatecOIDC instance to delegate to
- `Mcp__CanonicalResourceUri`: this server's own canonical resource URI

## Legal

- **Privacy Policy**: [/privacy.html](./wwwroot/privacy.html)
- **Terms of Service**: [/terms.html](./wwwroot/terms.html)
- **Company**: Scholtz & Company, j.s.a. (Slovakia)
- **Company ID**: 51882272
- **Tax ID**: 2120828105

## Support

- **General**: support@biatec.io
- **Privacy**: privacy@biatec.io
- **Legal**: legal@biatec.io

## License

This project is proprietary software owned by Scholtz & Company, j.s.a.
