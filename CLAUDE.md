# CLAUDE.md

This file guides Claude Code when working in this repository. It must stay in sync with
[.github/copilot-instructions.md](.github/copilot-instructions.md) — the two files serve the same purpose for
different AI assistants (Claude Code vs. GitHub Copilot). Whenever you update one, update the other to match.

## Project overview

Biatec — two independently deployed ASP.NET Core 10 services:

- **BiatecOIDC** is an OpenID Connect identity provider (JWT issuer) *and* a self-custody wallet API: whitelisted
  (or, for MCP-class clients, dynamically self-registered — see below) third-party apps authenticate users via
  Google or Microsoft Entra ID and receive Algorand-identity claims, and can sign Algorand transactions on the
  user's behalf via `POST /wallet/{network}/{address}/sign` (spend-limit-enforced, never handing out the
  user's own key material).
- **BiatecMCP** exposes that same self-custody wallet to AI assistants over the Model Context Protocol. It holds
  **no key material and no identity/storage-provider credentials of its own** — it is a pure OAuth 2.1 *resource
  server* that delegates all authentication and signing to BiatecOIDC (the *authorization server*), per the [MCP
  Authorization spec](https://modelcontextprotocol.io/specification/draft/basic/authorization) (RFC 9728
  Protected Resource Metadata + RFC 8707 resource indicators). See "MCP server" under Architecture notes below.

`BiatecOIDC` supports **two identity/storage providers** — Google (Drive) and Microsoft Entra ID (OneDrive app
folder) — presented as a picker (or skippable via `?idp=google`/`?idp=microsoft`, the "fast track") on its
`/select-provider` page. See `BiatecOIDC/ENTRA_SETUP_GUIDE.md` for the Entra app registration and
`Microsoft Graph Files.ReadWrite.AppFolder` permission this depends on.

`BiatecOIDC` has its own dedicated domain, `https://oidc.biatec.io` (the recommended host for new integrations),
and remains reachable via a carved-out set of paths on `https://google.biatec.io` too, as a legacy alias for
existing integrations (see "Kubernetes / ingress routing" below) — both hosts are internally self-consistent
since `JwtIssuerService.GetIssuer` derives the `iss` claim/discovery `issuer` from the actual request host rather
than a hardcoded value. `BiatecMCP` has its own dedicated domain too, `https://mcp.biatec.io` (MCP endpoint
`https://mcp.biatec.io/mcp`) — the canonical resource URI its own tokens/PRM document use — and remains reachable
via `https://google.biatec.io` as a legacy alias as well. `BiatecOIDC` depends on one piece of shared self-custody
infrastructure, `BiatecSelfCustodyCore` (see below), for its own signing/identity work; `BiatecMCP` does **not** -
it talks to BiatecOIDC over plain HTTP, forwarding the caller's own bearer token, and has zero compile-time
dependency on `BiatecSelfCustodyCore`.

## Solution layout

- `BiatecSelfCustodyCore/` — shared class library (net10.0, `Microsoft.NET.Sdk`), referenced by both `BiatecMCP` and
  `BiatecOIDC`. Holds the security-sensitive self-custody code so it exists in exactly one place:
  - `Providers/ICloudStorageProvider.cs` — the extension point for adding a new cloud storage backend: `Name`,
    `DisplayName`, `RequiredScope`, `TryDownloadAsync`/`UploadAsync` (by file name + bearer token),
    `HasWriteAccessAsync`, `GetAmbientAccessTokenAsync` (resolves the current cookie-signed-in user's token for
    this provider). `GoogleCloudStorageProvider.cs` and `MicrosoftCloudStorageProvider.cs` are the two
    implementations today (Google Drive folder search/create/read/write via `GoogleCredential`; OneDrive app-folder
    special folder via Graph REST, no SDK) — each owns its own storage transport *and* its own write-access scope
    check (there is no separate verifier class to keep in sync).
  - `Providers/ICloudStorageProviderCatalog.cs` + `CloudStorageProviderCatalog.cs` — resolves a provider by name
    (case-insensitive; unknown/empty name falls back to `GoogleCloudStorageProvider.ProviderName`, so
    pre-Microsoft-support sessions with no provider recorded keep working) and exposes `All` for the picker UIs to
    render buttons from. Built from `IEnumerable<ICloudStorageProvider>` resolved via DI.
  - `Providers/CloudStorageProviderClaims.cs` — `Stamp(principal, providerName)`, used by every OIDC scheme's
    `OnTokenValidated` to record which provider a session signed in with, as the `biatec_idp` claim.
  - `Repository/ICloudAccountRepository.cs` + `CloudAccountRepository.cs` — the thing services actually inject;
    owns the AES encrypt/decrypt + ARC76 account-derivation logic **once**, resolves the right
    `ICloudStorageProvider` via the catalog by name, and reads the access token either explicitly (device-pairing
    path) or ambiently (`provider.GetAmbientAccessTokenAsync()` for the cookie-session path).
  - `BusinessLogic/IDriveService.cs`, `DriveService.cs` — sign transactions, get account address (both take a
    provider name `string`)
  - `BusinessLogic/OpenIdConnectIncrementalAuth.cs` — shared `OnRedirectToIdentityProvider` logic (both apps, both
    schemes) for incremental-scope + forced-consent re-challenges
  - `Helper/AesEncryptionHelper.cs` — email-bound AES-256 encryption of the stored account
  - `Model/Configuration.cs` (app-wide host/Drive-storage-naming, bound from `App`), `GoogleCloudServiceConfiguration.cs`
    (Google OAuth client id/secret, bound from `CloudServices:Google`), `AesOptions.cs`, `MicrosoftEntraConfiguration.cs`
    (Entra app registration, bound from `CloudServices:Entra`), `AuthSchemeNames.cs` (just the `biatec_idp` claim type
    constant — each provider owns its own name via `ICloudStorageProvider.Name`)
- `BiatecMCP/` — the MCP server, an OAuth 2.1 resource server with **no BiatecSelfCustodyCore project
  reference and no Google/Microsoft/Redis dependency** (net10.0, `Microsoft.NET.Sdk.Web`)
  - `Program.cs` — `AddAuthentication` + `AddJwtBearer` (validates bearer tokens locally against BiatecOIDC's
    JWKS, `Authority` = `Oidc:Issuer`, `ValidAudience` = `Mcp:CanonicalResourceUri`) + `AddMcp` (serves
    `/.well-known/oauth-protected-resource`, shapes the 401/`WWW-Authenticate` challenge) — see
    `ModelContextProtocol.AspNetCore.Authentication.McpAuthenticationOptions`/`ProtectedResourceMetadata`.
    `AddAuthorizationBuilder().AddPolicy("sign", ...)` backs the `sign`-claim gate; `app.MapMcp("/mcp").RequireAuthorization()`.
  - `MCP/BiatecMCP.cs` — 19 MCP tools split into three chainable steps (build → sign → execute) rather than
    one monolithic call, so an unsigned transaction can be inspected, handed to a different signer, or
    combined as part of a multisig proposal before ever being broadcast:
    `listAlgorandAddresses`, `getBridgeConfiguration`, `listSupportedNetworks`, `getCryptoAddress`,
    `getCryptoBalance`, `getAddressInfo`, `listActiveAddresses` (read-only - `getAddressInfo`/
    `listActiveAddresses` and the four before them are
    chain-family-agnostic, see "EVM (Ethereum-family) support"/"Bitcoin/Bitcoin Cash support" below and
    "Address-centric wallet API and
    rekey support" in `BiatecOIDC`'s notes); `createPaymentTransaction`, `createOptInTransaction`,
    `createAssetCreateTransaction`,
    `createSwapTransaction`, `createBridgeTransaction` (a real [Aramid Finance](https://aramid.finance)
    bridge integration - see "Aramid bridge integration" below), `createMultisigTransaction` (build-only, no
    `sign` claim needed - see "Multisig transactions" below), `createBitcoinTransaction` (build-only, UTXO
    selection - see "Bitcoin/Bitcoin Cash support" below); `activateCryptoAddress` (registers a
    seed/slot → address pairing - the entry point for rekeying an external Algorand address to a
    Biatec-controlled key, requires `sign`); `signTransaction` (standalone - forwards
    to BiatecOIDC's `POST /wallet/{network}/{address}/sign`, requires `sign`), `mergeMultisigTransactions`
    (combines independently-signed multisig copies, no BiatecOIDC/Algod call); `executeAlgorandTransaction`
    (broadcasts already-signed Algorand transactions via algod, requires `sign`), `executeBitcoinTransaction`
    (broadcasts already-signed Bitcoin/Bitcoin Cash transactions via Blockchair, requires `sign`). Every
    tool's `[Description]` names the next
    tool in the
    chain, since MCP has no other side-channel for teaching the connected agent the intended protocol. All
    forward the caller's own bearer token to BiatecOIDC rather than touching any key material - see "MCP
    server" under Architecture notes below for the full request flow. The `create*`/`getCryptoAddress`
    tools accept optional `seedAddress`/`slot` parameters to build against/from a specific seed/ARC-76
    slot instead of the default identity (see BiatecOIDC's "Address-centric wallet API and rekey support"
    note) - `signTransaction` and `getAddressInfo` instead take the address itself, matching BiatecOIDC's
    address-centric wallet route shape; `activateCryptoAddress` takes `seedAddress`/`slot` as required
    parameters (route segments on the BiatecOIDC side) plus the `address` being activated (a body field on
    the BiatecOIDC side). Every `network` parameter across these tools
    (renamed from `genesisId` for consistency with this address-centric surface) resolves against the
    dynamic, liveness-verified `IAlgorandChainRegistry` (see "Multi-chain support" below) when it isn't one
    of the locally-configured `Algod:Networks` entries.
  - `BusinessLogic/IBiatecWalletClient.cs` + `BiatecWalletClient.cs` — typed `HttpClient` wrapping BiatecOIDC's
    `POST /wallet/{network}/{address}/sign`/`GET /wallet/seeds`/
    `GET /wallet/address/{seedAddress}/{slot}` (returns both the AVM and EVM address for that seed/slot in
    one call)/`GET /wallet/{network}/{address}/info`/
    `POST /wallet/{network}/{seedAddress}/{slot}/activate`/`GET /wallet/active-addresses`, forwarding the
    caller's
    bearer token; `WalletApiException` carries BiatecOIDC's `ProblemDetails` title/detail back to the tool
  - `BusinessLogic/IDexQuoteProvider.cs` + `BiatecRouterQuoteProvider.cs`/`FolksRouterQuoteProvider.cs`/
    `HaystackRouterQuoteProvider.cs` + `DexSwapAggregatorService.cs` — `createSwapTransaction`'s quote
    comparison (see "DEX swap aggregation" under Architecture notes below for the scope decision on which
    provider can actually build a transaction today)
  - `BusinessLogic/AlgorandChainRegistryModels.cs`, `IPublicAlgodDataSource.cs`/`PublicAlgodDataSource.cs`,
    `IAlgorandChainRegistry.cs`/`AlgorandChainRegistry.cs` — the dynamic multi-chain (AVM) registry (see
    "Multi-chain support" below); a separate, independently-implemented copy exists under `BiatecOIDC/`,
    per this repo's no-compile-time-coupling rule
  - `BusinessLogic/EvmChainRegistryModels.cs`, `IPublicEvmRpcDataSource.cs`/`PublicEvmRpcDataSource.cs`,
    `IEvmChainRegistry.cs`/`EvmChainRegistry.cs`, `INetworkResolver.cs`/`NetworkResolver.cs` — the EVM
    (Ethereum-family) chain registry and the AVM/EVM-unifying network-name resolver (see "EVM
    (Ethereum-family) support" below); `BiatecOIDC` has no copy of these - see that note for why
  - `BusinessLogic/IAramidBridgeConfigProvider.cs`/`AramidBridgeConfigProvider.cs`,
    `AramidBridgeModels.cs`, `Helper/AramidBridgeCalculator.cs` — Aramid Finance's live bridge
    configuration/fee math (see "Aramid bridge integration" below); backs both `getBridgeConfiguration` and
    `createBridgeTransaction`
  - `BusinessLogic/IPublicBitcoinDataSource.cs`/`BlockchairDataSource.cs`, `Helper/BitcoinTransactionBuilder.cs`,
    `Model/BitcoinModels.cs` — Bitcoin/Bitcoin Cash UTXO data source and coin-selection/fee-estimation logic
    (see "Bitcoin/Bitcoin Cash support" below); backs `getCryptoAddress`/`getCryptoBalance`'s Btc/Bch
    branches, `createBitcoinTransaction`, and `executeBitcoinTransaction`
  - `Helper/AlgorandTransactionBuilder.cs` — builds *unsigned* payment/asset-transfer/opt-in/asset-create
    transactions (Algorand4 SDK) and canonical-msgpack-encodes them for `/wallet/sign` - no key material
    ever touches this project
  - `Helper/MultisigTransactionBuilder.cs` — derives a multisig account's address from
    `(version, threshold, participantAddresses)`, builds the unsigned `SignedTransaction` "envelope" (a
    `Transaction` plus an empty-signature `MultisigSignature`) each cosigner independently signs, and merges
    N independently-signed copies via the Algorand4 SDK's `SignedTransaction.MergeMultisigTransactionBytes` -
    see "Multisig transactions" under Architecture notes below
  - `Model/` — `AlgodConfiguration`, `CorsConfiguration`, `Configuration` (local `App:Host`),
    `OidcConfiguration` (`Oidc:Issuer`), `McpResourceConfiguration` (`Mcp:CanonicalResourceUri`),
    `WalletApiModels` (DTOs mirroring BiatecOIDC's `WalletModels.cs`, duplicated rather than referenced so the
    two independently-deployed services share no compile-time coupling)
  - `Generated/OidcApiClient.g.cs` — the NSwag-generated client wrapping every HTTP call to BiatecOIDC's
    wallet API (see "Generated OIDC API client" under Architecture notes below for what it is and how to
    regenerate it); `BusinessLogic/BiatecWalletClient.cs` is the only file that references it directly
  - `wwwroot/` — static pages: `index.html`, `privacy.html`, `terms.html`
- `BiatecOIDC/` — the OIDC/JWT issuer web/API project (net10.0, `Microsoft.NET.Sdk.Web`)
  - `Controllers/JwtIssuerController.cs` — `/authorize` (+ `idp` fast track, + `resource` for RFC 8707), `/token`
    (+ `resource`), `/register` (RFC 7591 Dynamic Client Registration — public clients only, no secret ever
    issued; see "MCP server" under Architecture notes for why this exists), `/select-provider` (picker page,
    one button per provider registered in the catalog), `/authorize/challenge`, `/authorize/callback` (verifies
    storage-write access via `catalog.Resolve(idp).HasWriteAccessAsync(...)` before finalizing)
  - `Controllers/WalletController.cs` — `/wallet/{network}/{address}/sign` (`sign` claim, + `rekey` for a
    rekey transaction), `/wallet/limits` get (identity only)/put (`manage-limits` claim),
    `/wallet/{network}/{address}/limits` (same claims, per-address bucket),
    `/wallet/limits/currencies` (identity only), `/wallet/{network}/{address}/info` (identity only),
    `/wallet/{network}/{seedAddress}/{slot}/activate` (`sign` claim - `seedAddress`/`slot` are route
    segments, the address being activated is a body field - see "Address-centric wallet API and rekey
    support" below), `/wallet/active-addresses` (identity only - lists every currently-active address at
    once), `/wallet/seeds` (identity only - lists every
    seed's address, replacing the removed `/wallet/address` list endpoint), `/wallet/address/{seedAddress}/{slot?}`
    (identity only - derives both the AVM and EVM address for a seed/slot in one call, replacing the removed
    per-family `/wallet/evm/address`/`/wallet/evm/address/{seedAddress}/{slot?}` endpoints, see "EVM
    (Ethereum-family) support" below); same manual bearer-token pattern as `JwtIssuerController`'s
    `/userinfo` (not `[Authorize]` — see `.claude/skills/biatec-oidc-jwt/SKILL.md`)
  - `Controllers/ChainsController.cs` + `Model/ChainsModels.cs` — `GET /chains`, `[AllowAnonymous]`, no bearer
    token needed - the public, liveness-checked Algorand chain registry (see "Multi-chain support" below)
  - `wwwroot/chains.html` — the public per-chain-family feature matrix page (see "EVM (Ethereum-family)
    support" below); purely static, fetches `GET /chains` client-side, no dedicated backend code
  - `BusinessLogic/AlgorandChainRegistryModels.cs`, `IPublicAlgodDataSource.cs`/`PublicAlgodDataSource.cs`,
    `IAlgorandChainRegistry.cs`/`AlgorandChainRegistry.cs` — an independent copy of `BiatecMCP`'s own registry
    (see "Multi-chain support" below), duplicated rather than shared via `BiatecSelfCustodyCore` per this
    repo's no-compile-time-coupling rule
  - `BusinessLogic/JwtIssuerService.cs` (+ `IJwtIssuerService`) — depends on `BiatecSelfCustodyCore`'s
    `IDriveService` for the `primary_seed_address` claim - the current primary seed's own identifying
    (Algorand slot-0) address, resolved once at `/authorize` time (`IDriveService.GetPrimarySeedAddressAsync`,
    a thin wrapper over `ICloudAccountRepository.ResolveSeedAddressAsync(seedAddress: null)` - no ARC-76
    derivation, just whichever vault entry is `IsPrimary`) and cached through code exchange/every refresh,
    same "resolve once, never recompute" treatment as before. Deliberately **not** a per-chain-family derived
    address - an earlier version of this claim (`algorand_address`, later mirrored by a short-lived
    `evm_address`) cached the *derived* address directly, which meant `BiatecMCP`'s EVM address lookup had no
    equivalent fast path and always needed a live Drive-backed round trip, the actual root cause of a real
    reported bug ("what is my Algorand address" succeeded off the claim while "what is my Ethereum address"
    failed with a storage/seed error once the cached provider token had gone stale). The fix made both chain
    families resolve identically instead of patching the asymmetry: `BiatecMCP` always derives the real
    address via a live `GET /wallet/address/{seedAddress}/{slot}` call for both AVM and EVM (see
    `ResolveDerivedAddressAsync` under "EVM (Ethereum-family) support" below), using `primary_seed_address`
    only as the default *seed selector* when no explicit one is given - never as the address itself. Also
    stamps `biatec_idp`/`sign`/`manage-limits` claims onto issued access tokens. `ResolveClientAsync(clientId)`
    checks statically-configured `JwtIssuer:Clients`
    first, falling back to `IDynamicClientStore` (Redis-backed, RFC 7591-registered clients) — every client
    lookup in this file and `JwtIssuerController` goes through it, so a static entry always takes precedence
    over a same-`ClientId` dynamic one (how an operator hand-upgrades a self-registered MCP client's scopes)
  - `BusinessLogic/WalletService.cs` (+ `IWalletService`) — signs a transaction group via `IDriveService`, first
    pricing every `pay`/`axfer` in USD (`IAssetValuationService`) and checking the group's total against
    `ISpendingLimitService`'s daily/weekly/monthly limits, then recording the spend to the ledger after signing
  - `BusinessLogic/SpendingLimitService.cs` (+ `ISpendingLimitService`) — daily (trailing 24h)/weekly (trailing
    7d)/monthly (trailing 30d) per-user spending limits and a signed-transaction ledger, in a currency the user
    picks (defaults to USD); both AES-encrypted and stored in the user's own Drive/OneDrive (never Redis, never
    Biatec's servers in plaintext) via the same `ICloudStorageProviderCatalog`/`AesEncryptionHelper` primitives
    `CloudAccountRepository` uses for the account file itself
  - `BusinessLogic/BiatecRouterValuationService.cs` (+ `IAssetValuationService`, `IBiatecRouterQuoteClient`) —
    prices a spent asset in USD via the `BiatecRouterConnector` NuGet package's public `/quote` endpoint, quoting
    against `SpendingLimitsConfiguration.UsdReferenceAssetId` (mainnet USDC by default)
  - `BusinessLogic/CnbExchangeRateService.cs` (+ `IExchangeRateService`) — currency exchange rates for
    spending-limit configuration, from the Czech National Bank's daily fixing JSON API, cached in Redis
    (`ExchangeRateConfiguration.CacheDurationMinutes`); backs `GET /wallet/limits/currencies` and the USD→limit-
    currency conversion `SpendingLimitService` does when checking a limit
  - `BusinessLogic/CoinGeckoValuationService.cs` (+ `IBitcoinValuationService`, `BitcoinValuationException`) —
    prices a Bitcoin/Bitcoin Cash native-asset spend in USD via CoinGecko's public spot-price endpoint (see
    "Bitcoin/Bitcoin Cash support" below); no router involved, unlike `BiatecRouterValuationService` above,
    since the native coin *is* the asset
  - `BusinessLogic/ProviderAccessTokenProtector.cs` (+ `IProviderAccessTokenProtector`) — AES-256-GCM encrypts the
    caller's Google/Microsoft access token (under `ProviderTokenProtectionConfiguration`, a key dedicated to this
    - never `AesOptions`) so it can be cached inside issued access/refresh tokens; see the "Provider access token
    caching" architecture note below
  - `BusinessLogic/AddressActivationModels.cs`, `IAddressActivationService.cs`/`AddressActivationService.cs` —
    the address → `(seedAddress, slot)` activation registry backing `/wallet/{network}/{address}/info` and
    `/wallet/{network}/{seedAddress}/{slot}/activate`, AES-encrypted on the user's own Drive/OneDrive under its own
    `AddressActivations.%AESID%.dat` file (see "Address-centric wallet API and rekey support" below)
  - `BusinessLogic/INetworkResolver.cs`/`NetworkResolver.cs` — `BiatecOIDC`'s own lightweight `network`
    route-segment resolver (AVM via the existing `IAlgorandChainRegistry`, EVM as name-recognition-only, no
    live EVM chain talk); a separate, independently-implemented copy from `BiatecMCP`'s own `INetworkResolver`,
    per this repo's no-compile-time-coupling rule
  - `Helper/RedirectUriMatcher.cs` — OIDC redirect URI matching incl. wildcard support
  - `Helper/AlgorandTransactionInspector.cs` — decodes a transaction's raw msgpack to find its real type/amount/
    asset id (generic map peek first — a `Transaction` subclass's `type` property is a hardcoded constant, not
    decoded off the wire)
  - `Helper/BearerTokenHelper.cs` — shared `Authorization: Bearer` header extraction (`JwtIssuerController` +
    `WalletController`)
  - `Model/JwtIssuerModels.cs`, `Model/WalletModels.cs`, `Model/ProviderTokenProtectionConfiguration.cs`, plus
    local `RedisConfiguration`/`CorsConfiguration` copies
  - `wwwroot/index.html` — the OIDC/wallet API documentation site, served at `/` (reachable on `oidc.biatec.io`'s
    own Ingress; not reachable via the `google.biatec.io` alias, which only carves out this app's protocol paths)
  - `OIDC_INTEGRATION_GUIDE.md`, `BIATEC_OIDC_LOGOUT_REQUIREMENTS.md`, `ENTRA_SETUP_GUIDE.md`
  - `MOCK_TESTING.md` — internal-only: a mock `ICloudStorageProvider` (`BiatecSelfCustodyCore/Providers/MockCloudStorageProvider.cs`/`MockCloudStorage.cs`) that runs the *real* OIDC authorize/token flow against deterministic ARC-76 test accounts configured under `CloudServices:Mock` (disabled by default), so other apps can be tested end-to-end without a real Google/Microsoft sign-in. Deliberately never linked from the public integration docs — see that file before touching `CloudServices:Mock`, `MockCloudStorageProvider.cs`, `ICloudAccountRepository.SeedTestVaultAsync`, or the `Mock*`-prefixed members on `JwtIssuerController`.
- `BiatecMCPTests/` — NUnit + Moq tests for `BiatecMCP` (OAuth resource-server wiring via
  `Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory`, `AlgorandTransactionBuilder`, `BiatecWalletClient`,
  the MCP tools) **and** `BiatecSelfCustodyCore` (AES encryption, transfer policy, `CloudStorageProviderCatalog`,
  `GoogleCloudStorageProvider`, `MicrosoftCloudStorageProvider`, plus a shared `FakeCloudStorageProvider` test
  double) — the latter group stays here even though the `BiatecMCP` *product* project no longer references
  `BiatecSelfCustodyCore`, since `BiatecOIDCTests` doesn't yet have its own copies and this is still the only
  place that functionality is exercised
- `BiatecOIDCTests/` — NUnit + Moq tests for `BiatecOIDC` (JWT issuer service + controller, including Dynamic
  Client Registration and RFC 8707 resource-indicator handling)

Historical note: this used to be one project, `AlgorandGoogleDriveAccount` (with a test project
`AlgoranGoogleDriveAccountTests` that intentionally dropped one "d" from "Algorand"). It was split into the three
projects above; `BiatecMCP` inherited that original project's git history since it kept the most files.

## Build, test, run

```bash
dotnet build Biatec.slnx
dotnet test BiatecMCPTests/BiatecMCPTests.csproj
dotnet test BiatecOIDCTests/BiatecOIDCTests.csproj
dotnet run --project BiatecMCP/BiatecMCP.csproj
dotnet run --project BiatecOIDC/BiatecOIDC.csproj
```

`BiatecOIDC` requires Redis (`Redis:ConnectionString`), Google OAuth 2.0 credentials
(`CloudServices:Google:ClientId`/`ClientSecret`), and Microsoft Entra ID credentials
(`CloudServices:Entra:TenantId`/`ClientId`/`ClientSecret` — see `BiatecOIDC/ENTRA_SETUP_GUIDE.md`) to run.
`BiatecMCP` requires none of that — only `Oidc:Issuer` (which BiatecOIDC instance to delegate to) and
`Mcp:CanonicalResourceUri` (its own resource identity); both default to production values in
`BiatecMCP/appsettings.json` for local development against the live BiatecOIDC.

CI is two separate GitHub Actions workflows, not one — nothing pushed to `master` reaches production
automatically. `.github/workflows/deploy-stage.yml` builds/pushes both Docker images and deploys them
straight to the **stage** environment on every push to `master`. `.github/workflows/promote-production.yml`
is manually triggered (`workflow_dispatch`) and re-deploys an already-built, already-stage-tested image tag
to **production** — it never rebuilds anything. See [docs/STAGE_ENVIRONMENT.md](docs/STAGE_ENVIRONMENT.md)
for the full stage/production architecture and [docs/CICD_GITHUB_ACTIONS.md](docs/CICD_GITHUB_ACTIONS.md)
for the required GitHub secrets (shared by both workflows) and
[docs/KUBE_CONFIG_SECURITY.md](docs/KUBE_CONFIG_SECURITY.md) for why the CI kubeconfig is namespace-scoped
and short-lived. There is no automated test job in CI, so run tests locally before pushing.

Root `.editorconfig` + `Directory.Build.props` (auto-imported by all 5 `.csproj`s) enable the built-in Roslyn
analyzers solution-wide and promote `IDE0005` (unused usings) to a build error. Run `dotnet format Biatec.slnx` to
fix flagged style/unused-using issues, and `dotnet format Biatec.slnx --verify-no-changes` before committing —
both should be clean.

## Kubernetes / ingress routing

Both services run as separate Deployments/Services in the `biatec` namespace. `BiatecMCP` owns the
`google.biatec.io` host as a legacy catch-all **and** has its own dedicated `mcp.biatec.io` host (its canonical
OAuth resource URI, `Mcp:CanonicalResourceUri` = `https://mcp.biatec.io/mcp`); `BiatecOIDC` is reachable on its
own dedicated `oidc.biatec.io` host **and** via a carved-out set of paths on `google.biatec.io` (a legacy alias,
kept working for integrations set up before `oidc.biatec.io` existed) — four Ingress objects total across the two
deployment manifests:

- `k8s/main/deployment-mcp.yaml` — `biatec-mcp-app-deployment`/`biatec-mcp-service`, plus two Ingress objects:
  - `biatec-mcp-ingress` — catch-all path (`/(.*)`, `rewrite-target: /$1`) on `google.biatec.io` — this is the
    default backend for the host, so `/mcp` and all static `wwwroot` pages keep resolving here unchanged (legacy
    alias; not the canonical resource host anymore).
  - `biatec-mcp-domain-ingress` — the dedicated `mcp.biatec.io` host, same catch-all shape, its own
    `tls-mcp.biatec.io` TLS entry — this is BiatecMCP's canonical resource identity per `Mcp:CanonicalResourceUri`
    and what its `/.well-known/oauth-protected-resource` document/JWT audience validation expect.

  Any Ingress using this regex-catch-all idiom (both of the above, `biatec-oidc-domain-ingress`, and all
  `k8s/stage/*` Ingresses) needs **both** `nginx.ingress.kubernetes.io/use-regex: "true"` **and**
  `pathType: ImplementationSpecific` on that path — `pathType: Prefix` means a literal path-segment match per
  the Ingress spec, so ingress-nginx's admission webhook rejects a regex path there even with `use-regex` set
  (`path /(.*) cannot be used with pathType Prefix`). The literal/`Exact`-path `biatec-oidc-ingress` below needs
  neither, since none of its paths are regexes.
- `k8s/main/deployment-oidc.yaml` — two Ingress objects for `biatec-oidc-app-deployment`/`biatec-oidc-service`:
  - `biatec-oidc-ingress` — claims only the OIDC-specific literal paths on the shared `google.biatec.io` host
    (`/.well-known`, `/authorize`, `/token`, `/userinfo`, `/introspect`, `/verify`, `/connect/endsession`,
    `/logout`, `/select-provider`, `/oidc/signin-google`, `/oidc/signin-microsoft`), no rewrite. nginx-ingress
    matches literal/prefix locations ahead of `biatec-mcp-ingress`'s regex catch-all regardless of object order, so
    this reliably carves out just those paths without touching anything else on that host.
  - `biatec-oidc-domain-ingress` — the whole `oidc.biatec.io` host, full catch-all (`/(.*)`, `rewrite-target: /$1`,
    same idiom as `biatec-mcp-ingress`) straight to `biatec-oidc-service`, with its own TLS entry/secret
    (`tls-oidc.biatec.io`). Kept as a separate Ingress object rather than an extra host block on
    `biatec-oidc-ingress`, because the `rewrite-target` annotation a catch-all needs applies Ingress-object-wide
    and would otherwise also change how that Ingress's literal/`Exact` paths for `google.biatec.io` are matched.

  `BiatecOIDC`'s Google **and** Microsoft OIDC handlers use non-default `CallbackPath`s (`/oidc/signin-google`,
  `/oidc/signin-microsoft`) specifically so they land on this deployment and not on `BiatecMCP`'s catch-all (which
  can't decrypt this app's correlation cookie — separate processes, no shared Data Protection key ring). Both
  callback paths work on both hosts (`google.biatec.io` and `oidc.biatec.io`) since they're just paths, not
  host-specific — but each is a *distinct redirect URI* as far as Google/Entra's own app-registration allowlists
  are concerned, so adding `oidc.biatec.io` as a new host means also adding
  `https://oidc.biatec.io/oidc/signin-google` (Google Cloud Console OAuth client) and
  `https://oidc.biatec.io/oidc/signin-microsoft` (Entra app registration, see `BiatecOIDC/ENTRA_SETUP_GUIDE.md`)
  there — external, one-time, manual steps outside this repo. `BiatecMCP` keeps the framework's default
  `/signin-google` and a `/signin-microsoft` CallbackPath, both fine as-is since its ingress is the catch-all.

  Neither host hardcodes `JwtIssuer:Issuer` in `k8s/main/conf-oidc/appsettings.json` — deliberately: leaving it
  unset means `JwtIssuerService.GetIssuer` derives the `iss` claim/discovery `issuer` from whichever host actually
  received the request, so `oidc.biatec.io` and the `google.biatec.io` alias each stay internally self-consistent.
  Setting a static `Issuer` there would fix `iss` to one value and break discovery on whichever host *isn't* that
  value (its `/.well-known/openid-configuration` would advertise an `issuer` that doesn't match the host it was
  fetched from — a mismatch strict OIDC clients reject). Do not add a static `Issuer` to that ConfigMap without
  re-checking this reasoning.

  `JwtIssuer:ProtectedResources` (RFC 8707 — see `JwtIssuerConfiguration.ProtectedResources`'s remarks) must
  include BiatecMCP's canonical resource URI (`https://mcp.biatec.io/mcp` in production,
  `https://stage.mcp.biatec.io/mcp` in stage) for BiatecMCP's bearer-token audience validation to accept tokens
  BiatecOIDC issues. Production's value lives in the `google-account-main-app-secret` Secret alongside the rest
  of `JwtIssuer:*` (see the comment in `k8s/main/conf-oidc/appsettings.json`); stage sets it directly in
  `k8s/stage/conf-oidc-stage/appsettings.json`.

Both deployments reuse the same secrets (`google-account-main-app-secret` for app config,
`csharp-cert`/`csharp-cert-password` for the internal Kestrel HTTPS cert) — there was no need to provision new
ones. Config is split per-service: `k8s/main/conf-mcp/` / `biatec-mcp-conf` and `k8s/main/conf-oidc/` /
`biatec-oidc-conf`.

## Stage environment

`k8s/stage/` mirrors `k8s/main/` for both services, at `stage.google.biatec.io` / `stage.mcp.biatec.io` /
`stage.oidc.biatec.io`, in the **same `biatec` namespace** with `-stage`-suffixed resource names
(not a separate namespace — the existing namespace-scoped CI `Role` grants verbs on resource
*types*, so stage needed no new RBAC). `deploy-stage.yml` deploys here on every push to `master`;
`k8s/main/*` (production) only changes via the manually-triggered `promote-production.yml`. Stage
uses its own dedicated Kubernetes Secret, `biatec-stage-app-secret` — never production's
`google-account-main-app-secret` — generated once via
[k8s/stage/generate-stage-secret.sh](k8s/stage/generate-stage-secret.sh), which always mints a
fresh AES key and JWT signing key dedicated to stage (never copied from production). Self-custody
files are further isolated on top of that by `App:StorageFolderName` being `"BiatecStage"` there
(vs `"Biatec"` in production), set directly in the stage ConfigMaps. See
[docs/STAGE_ENVIRONMENT.md](docs/STAGE_ENVIRONMENT.md) for the full picture, including what is
*not* isolated between stage and production by default (Redis, unless you point
`generate-stage-secret.sh` at a separate instance/DB index) and the one-time DNS/OAuth-redirect-URI
setup stage needs.

## Architecture notes

- **Self-custody model**: Algorand private keys are encrypted per-email via `AesEncryptionHelper`
  (`BiatecSelfCustodyCore`) and stored as a file (`AVMAccount.%AESID%.dat` by default — see "AES key-ring
  rotation" below for the `%AESID%` placeholder) in the user's own Google Drive folder or OneDrive app folder,
  depending which provider they signed in with. Biatec servers only decrypt in-memory during an explicitly
  authorized signing operation — never persist plaintext keys.
- **ARC-76 package provenance**: the actual ARC-76 (deterministic, password/email-derived account)
  derivation lives in two small third-party packages, `ARC76Account.Algorand` and `ARC76Account.Ethereum`
  (`BiatecSelfCustodyCore.csproj`) - successors to the now-retired single-package `AlgorandARC76Account` this
  repo used before, split per chain family (a shared `ARC76Account.Core` package is a transitive dependency
  of both, never referenced directly here). Both expose a type named `ARC76` with a `GetEmailAccount(email,
  mnemonic, slot)` method - same method name in both, disambiguated only by namespace, so
  `CloudAccountRepository.cs` aliases them (`using AlgorandArc76 = ARC76Account.Algorand.ARC76;` /
  `using EthereumArc76 = ARC76Account.Ethereum.ARC76;`) rather than importing both namespaces unqualified.
  `ARC76Account.Algorand.ARC76.GetEmailAccount` returns an Algorand4 SDK `Account` (identical to the
  predecessor package's `GetEmailAccount`); `ARC76Account.Ethereum.ARC76.GetEmailAccount` returns a
  `Nethereum.Signer.EthECKey` (identical to the predecessor's `GetEVMEmailAccount`, just renamed since it's
  no longer sharing a type with the Algorand method). `ARC76Account.Ethereum` depends on `Nethereum.Signer`
  6.1.0 (up from the ~5.0.0 the predecessor package resolved), which transitively bumps
  `Nethereum.Model`/`Nethereum.RLP`/`Nethereum.Hex`/`Nethereum.Util` too - verified compatible with every
  `Nethereum.Model`/`Nethereum.Signer` API `DriveService.SignEvmTransactionAsync` (see "EVM transaction
  signing" below) depends on before switching, and confirmed by that feature's own real
  sign-then-recover-sender round-trip tests (`DriveServiceTests`) passing unchanged after the bump.
- **Multi-seed vault and on-chain rekey**: the account file's decrypted content is a `SeedVault`
  (`BiatecSelfCustodyCore.Model`) — a list of independently-generated `SeedVaultEntry` seeds, each identified
  by its own ARC-76 slot-0 address (`SeedAddress`), with exactly one flagged `IsPrimary` at a time.
  `CloudAccountRepository.LoadAccountAsync` always derives from whichever seed is primary (`slot` still
  parameterizes derivation *within* that seed, exactly as before this existed); an existing plain-mnemonic
  file (pre-dating this feature) is transparently wrapped into a single-seed vault the first time it's read,
  same "migrate on read" philosophy as the AES key-ring rotation below. Seeds are **never deleted** — a
  since-superseded seed may still authorize the account on a different network, or be part of a multisig
  configured outside Biatec. `WalletController` (`BiatecOIDC`) exposes this as `GET /wallet/seeds` (list,
  `openid` only), `POST /wallet/seeds` (mint a new seed, requires the `rekey` claim — it's the first step of
  recovering from a suspected key compromise), and `PUT /wallet/seeds/primary` (switch which seed is primary,
  requires `sign`). Biatec never builds or submits the on-chain rekey transaction itself — the RP's own
  backend builds a transaction with Algorand's `rekey` field set to the new seed's address and submits it
  through the existing `POST /wallet/{network}/{address}/sign`, which is what actually enforces the `rekey` claim: see the
  "Wallet API" bullet below and `AlgorandTransactionInspector`'s `IsRekey` detection. Only once that
  transaction is confirmed on-chain should the caller call `PUT /wallet/seeds/primary` — switching primary
  before that would make Biatec sign with a key the account no longer recognizes.
  **Renaming a `SeedVaultEntry` property is a breaking data-migration hazard, not just a code change**: this
  class has no `[JsonPropertyName]` attributes, so a plain C# property name *is* its persisted JSON key. A
  same-day rename of `PrimaryAddress` → `SeedAddress` (no migration written) meant every vault file persisted
  under the old name still had JSON key `"primaryAddress"` after that rename deployed — `SeedAddress` silently
  deserialized to its `string.Empty` default instead of failing loudly, breaking every by-address lookup
  (`getCryptoAddress`, sign, activate) for seeds created before the rename with no error pointing at the cause.
  `CloudAccountRepository.LoadVaultOrEmptyAsync` now calls `HealMissingSeedAddressesAsync` after every parse —
  since `SeedAddress` is always deterministically re-derivable from `Mnemonic` (the same computation
  `BuildSeedEntry` uses), any entry found with an empty `SeedAddress` but an intact `Mnemonic` is recomputed
  and the vault is re-saved once, self-healing on next read with no manual intervention. Renaming (or removing)
  a property on `SeedVaultEntry`, `AddressActivationEntry`, `SpendingLimitSettings`/`SpendingLedgerEntry`, or
  any other type serialized straight to a user's own persisted file needs either a compatible migration path
  like this one or an explicit `[JsonPropertyName]` alias for the old key — never a bare rename.
- **Cross-cloud vault backup**: an explicit, user-triggered copy of the encrypted vault file from a user's
  primary cloud provider to a second one they separately authorize (`IVaultBackupService`/`VaultBackupService`,
  `VaultBackupController`, all `BiatecOIDC`) — mitigates losing the keys to a ban or forgotten credentials on
  a single cloud account. Nothing here is automatic; it's a three-step flow (`POST /wallet/backup/start`
  requires `sign`, a browser round-trip through `GET /wallet/backup/authorize`/`callback`, then
  `POST /wallet/backup/complete` requires `sign`), Redis-backed (`vaultbackup:pending:`/`vaultbackup:linked:`
  prefixes, mirroring `JwtIssuerService`'s one-time-use record pattern). Deliberately does **not** use the
  normal `Challenge()`/OIDC-scheme sign-in flow to authorize the second provider — that would re-fire the
  scheme's `OnTokenValidated` and overwrite the user's real `biatec_idp` cookie claim — instead it drives a
  manual OAuth2 authorization-code round trip via two new `ICloudStorageProvider` members,
  `BuildAuthorizationUrl`/`ExchangeAuthorizationCodeAsync` (each provider owns its own OAuth specifics, same
  extension-point philosophy as the rest of the interface). The second provider's access token is used exactly
  once (to copy the file) and is never cached or persisted beyond that.
- **AES key-ring rotation**: `AesOptions` (self-custody file, shared by both apps) and `ProviderTokenProtection`
  (`BiatecOIDC`'s cached provider access/refresh tokens) are each a rotatable key ring
  (`BiatecSelfCustodyCore.Model.IAesKeyRingConfiguration`: an `ActiveKeyId` plus a `Keys[]` list of
  `{KeyId, Key, IV}` generations) rather than a single `{Key, IV}` pair — resolved via
  `AesKeyRingResolver.GetActiveKey`/`GetHistoricalKeys` (`BiatecSelfCustodyCore/Helper/`), which fail fast
  outside `Development` if `ActiveKeyId` doesn't resolve to valid key material, same precedent as
  `JwtIssuerService.LoadOrCreateSigningKey`. Rotating is additive: generate a new key/IV, add it as a new
  `Keys[]` entry with a new `KeyId`, flip `ActiveKeyId` to it, and keep old entries around for as long as old
  data might still need them — nothing is ever silently orphaned. For the self-custody account file and
  `SpendingLimitService`'s limits/ledger files, `%AESID%` in the configured filename is a hash of whichever
  key generation encrypted that specific file (`AesEncryptionHelper.MakeAesId`), so each generation's data
  lives under its own distinct name; `EncryptedKeyRingFileStore.LoadAsync` (`BiatecSelfCustodyCore/Helper/`)
  tries the active generation's filename first, then each historical generation's filename in turn, and the
  moment a historical-generation file is found, immediately re-encrypts it under the active key, re-uploads it
  under the active filename, and best-effort deletes the stale file (`ICloudStorageProvider.DeleteAsync`) — so
  data migrates onto the new key the next time it's touched, not via a batch job, and a rotation never risks
  silently creating a *new* account when the old file just isn't found under the new key's name (the bug this
  design fixes). `ProviderAccessTokenProtector` has no filename to key off (its ciphertext lives inside a JWT
  claim) so it instead tries the active key then every historical key in turn on decrypt — safe because it
  only ever writes the authenticated AES-GCM format, where a wrong key deterministically fails the auth-tag
  check; there's nothing to write back for it, since the next issued/refreshed Biatec token naturally gets a
  fresh claim encrypted under whatever is active by then (see `JwtIssuerService.RenewProviderTokenAsync`).
  Rotating requires a `kubectl rollout restart` of the affected deployment(s) since these keys arrive as plain
  environment variables (`envFrom: secretRef`), which `IOptionsMonitor<T>` cannot hot-reload — a rolling
  restart across replicas is already zero-downtime, so this doesn't need in-process hot-reload plumbing. See
  `k8s/stage/generate-stage-secret.sh`'s rotation runbook comment and
  `BiatecOIDC/OIDC_INTEGRATION_GUIDE.md`'s "Key rotation" section.
- **Pluggable cloud storage providers**: `ICloudStorageProvider` (`BiatecSelfCustodyCore/Providers/`) is the single
  extension point for a new storage backend — implement it, register it in DI, done; `ICloudAccountRepository`
  and `BiatecOIDC`'s `/select-provider` picker UI resolve providers dynamically and need zero code changes for
  provider #3+ (`BiatecMCP` has no storage providers of its own at all — see "MCP server" above). To add one:
  implement `ICloudStorageProvider` (including `DeleteAsync` — best-effort, never throws, used only to clean up
  a file just migrated to a new AES key generation), register it in `BiatecOIDC/Program.cs` the same way as the
  existing Google/Microsoft blocks (`AddHttpClient<T>()` +
  `AddScoped<ICloudStorageProvider>(sp => sp.GetRequiredService<T>())`), then add a matching authentication scheme
  block (copy the Microsoft `AddOpenIdConnect(...)` block as a template) whose `OnTokenValidated` calls
  `CloudStorageProviderClaims.Stamp(context.Principal, YourProvider.ProviderName)`.
- **Dynamic Client Registration (RFC 7591)**: `POST /register` (`BiatecOIDC`) lets any OAuth client self-register
  a public client_id at connect time — the mechanism MCP clients use (see "MCP server" above), since an operator
  can't hand-register every AI-assistant vendor's redirect URI in advance the way `JwtIssuer:Clients` normally
  requires. `IJwtIssuerService.RegisterDynamicClientAsync` validates `redirect_uris` (HTTPS or allowed loopback,
  same policy `/authorize` applies), rejects anything but `token_endpoint_auth_method: "none"` (no secret is ever
  issued here), and caps the registered client's `AllowedScopes` to
  `JwtIssuer:DynamicClientRegistrationDefaultScopes` regardless of what was requested — deliberately excludes
  `manage-limits`/`rekey`, so a self-registered client can never obtain Biatec's two highest-privilege wallet
  scopes without an operator hand-upgrading that specific `client_id` afterwards via a static `JwtIssuer:Clients`
  entry with the same id (which always wins — see `IJwtIssuerService.ResolveClientAsync`). Persisted in Redis via
  `IDynamicClientStore`, no expiry.
- **Multi-provider auth**: `BiatecOIDC` independently configures two authentication schemes — Google via
  `Google.Apis.Auth.AspNetCore3` (`AddGoogleOpenIdConnect`) and Microsoft Entra ID via the plain
  `AddOpenIdConnect(MicrosoftCloudStorageProvider.ProviderName, ...)` handler pointed at
  `https://login.microsoftonline.com/{TenantId}/v2.0` — both sign into the same cookie scheme, so `[Authorize]`
  endpoints don't care which provider was used. Google scopes: `openid profile email` +
  `DriveService.Scope.DriveFile`. Microsoft scopes: `openid profile email offline_access` +
  `https://graph.microsoft.com/Files.ReadWrite.AppFolder`. Each scheme's `OnTokenValidated` stamps a `biatec_idp`
  claim (`AuthSchemeNames.IdpClaimType`, via `CloudStorageProviderClaims.Stamp`) onto the signed-in principal so
  later code knows which storage backend to use. See `BiatecOIDC/ENTRA_SETUP_GUIDE.md` for the Entra app
  registration this depends on. `BiatecMCP` has no identity/storage-provider auth of its own at all — see "MCP
  server" below.
- **Provider picker / fast track**: a user chooses Google or Microsoft via `BiatecOIDC`'s `/select-provider` page
  (dynamically rendered, one button per provider in the catalog); can be skipped with `?idp=google`/
  `?idp=microsoft` on `/authorize`. Before finalizing, `catalog.Resolve(idp).HasWriteAccessAsync(accessToken)`
  confirms the fresh token actually has storage-write access (declining just that consent checkbox is possible
  even while completing sign-in); if missing, the browser is sent through one incremental-consent round-trip
  (forced fresh consent screen, `OpenIdConnectIncrementalAuth`) before the OIDC code is finalized, capped at one
  retry to avoid loops.
- **MCP server**: `BiatecMCP` is a pure OAuth 2.1 *resource server* (RFC 9728 Protected Resource Metadata + RFC
  8707 resource indicators) that delegates all authentication and signing to `BiatecOIDC` — it holds no key
  material, no Google/Microsoft credentials, and no session state of its own (replaced an earlier
  home-grown "device pairing" scheme entirely). Request flow: (1) an unauthenticated `POST /mcp` gets a `401`
  with `WWW-Authenticate: Bearer resource_metadata="https://mcp.biatec.io/.well-known/oauth-protected-resource"`
  (`Program.cs`'s `AddMcp`/`ProtectedResourceMetadata`, `ModelContextProtocol.AspNetCore.Authentication`); (2)
  the MCP client discovers `BiatecOIDC` as the authorization server from that document, and — since BiatecMCP
  has no pre-registered relationship with arbitrary MCP clients — self-registers via `POST /register` (RFC 7591
  Dynamic Client Registration, `IJwtIssuerService.RegisterDynamicClientAsync`/`IDynamicClientStore`, public
  client only, scopes capped to `JwtIssuer:DynamicClientRegistrationDefaultScopes`); (3) the client completes
  `/authorize` (PKCE, `resource=https://mcp.biatec.io/mcp`) + `/token`, receiving an access token whose `aud`
  contains **both** its own `client_id` and the resource URI (`JwtIssuerService.CreateAccessToken`'s RFC 8707
  handling) — this is what lets `BiatecMCP` validate tokens from *any* dynamically-registered client against one
  stable audience value via local JWT validation (`AddJwtBearer`, `Authority` = `Oidc:Issuer`), with no
  per-request network call to BiatecOIDC; (4) the tools (`MCP/BiatecMCP.cs`) forward that same bearer token
  to BiatecOIDC's wallet REST API (`IBiatecWalletClient`/`BiatecWalletClient`, internally wrapping the
  NSwag-generated `Generated.OidcApiClient` - see the "Generated OIDC API client" note below) —
  `getCryptoAddress` (the one canonical address-lookup tool - a former separate `getAlgorandAddress` tool
  was removed as pure duplication, since an Algorand-family address is just `getCryptoAddress(network:
  "Algorand")`) always derives the real address via a live `GET /wallet/address/{seedAddress}/{slot?}` call
  (`ResolveDerivedAddressAsync`), using the bearer token's own `primary_seed_address` claim as the default
  seed selector when none is given (falling back to `GET /wallet/seeds`'s primary seed if that claim is
  absent). That claim is captured once at token issuance and carried forward unchanged across refreshes (see
  `JwtIssuerService`'s refresh handling), so a long-lived token can end up caching a seed selector that no
  longer resolves - `ResolveDerivedAddressAsync` self-heals a `seed_not_found` response for a claim-derived
  selector by retrying against `GET /wallet/seeds`'s live primary seed, rather than surfacing that error
  outright; a *caller*-supplied `seedAddress` is never second-guessed this way. `listAlgorandAddresses` lists every seed via
  `GET /wallet/seeds`; any non-default identity hits `GET /wallet/address/{seedAddress}/{slot?}` the same way
  - each such derive call also
  activates the derived address(es) on BiatecOIDC's side (see "Address-centric wallet API and rekey support"
  below), so it's immediately usable by address alone afterwards. Wallet operations are three
  separate, chainable tools rather than one monolithic call: `createPaymentTransaction`/
  `createOptInTransaction`/`createAssetCreateTransaction`/`createSwapTransaction`/`createMultisigTransaction`
  build an *unsigned* transaction locally (`AlgorandTransactionBuilder`/`MultisigTransactionBuilder`,
  Algorand4 SDK, no key material touched, no `sign` claim required - building a proposal is harmless);
  `signTransaction(unsignedTransactions, network, address)` forwards it to
  `POST /wallet/{network}/{address}/sign` — (BiatecOIDC resolves `address` to the signing seed/slot,
  enforces the `sign`/`rekey` claim and both spending-limit tiers there —
  `McpTransferLimitsConfiguration`/`TransferPolicy` were removed from `BiatecMCP` entirely, since
  `/wallet/{network}/{address}/sign` already does this uniformly for every relying party) and requires the
  `sign` claim itself; `executeAlgorandTransaction` broadcasts the signed bytes to Algod via a shared private
  `SubmitSignedTransactionsAsync` helper, also requiring `sign`. `mergeMultisigTransactions` combines
  independently-signed copies of a `createMultisigTransaction` envelope (no BiatecOIDC/Algod call - pure
  local combination via the Algorand4 SDK). `createBridgeTransaction` builds a real Aramid Finance bridge
  transaction (see "Aramid bridge integration" below). `getAddressInfo(network, address)`,
  `listActiveAddresses()`, and `activateCryptoAddress(network, seedAddress, slot, address)` are thin
  wrappers over `GET /wallet/{network}/{address}/info`, `GET /wallet/active-addresses`, and
  `POST /wallet/{network}/{seedAddress}/{slot}/activate` respectively — `activateCryptoAddress`'s own
  `[Description]`
  spells out the full external-rekey flow end to end (mint a spare seed via the existing seed tools, submit
  and confirm the on-chain rekey transaction outside Biatec, then call this to register the pairing). Every
  tool's
  `[Description]` names the next tool in the intended chain, since MCP has no other side-channel for this.
  Mounted at `/mcp` via `ModelContextProtocol.AspNetCore`
  (`AddMcpServer().WithHttpTransport().WithToolsFromAssembly().AddAuthorizationFilters()`), stateless HTTP
  transport.
- **Generated OIDC API client**: every HTTP call `BiatecMCP` makes to BiatecOIDC's wallet API goes through
  `BiatecMCP/Generated/OidcApiClient.g.cs` - a client generated by the NSwag CLI (`nswag openapi2csclient`)
  from BiatecOIDC's own published OpenAPI document, not hand-written request/response plumbing. Regenerate it
  whenever `WalletController`'s request/response shapes change: build `BiatecOIDC`, generate a fresh spec with
  `swagger tofile --output <path> BiatecOIDC/bin/Debug/net10.0/BiatecOIDC.dll v1` (`Swashbuckle.AspNetCore.Cli`
  - this works without a live Redis/OAuth-configured environment, since Swagger generation only reflects over
  controller/DTO types, never resolves a controller instance), then regenerate the client with
  `nswag openapi2csclient /input:<spec> /classname:OidcApiClient /namespace:BiatecMCP.Generated
  /output:Generated/OidcApiClient.g.cs /GenerateClientInterfaces:true /InjectHttpClient:true /UseBaseUrl:false
  /GenerateExceptionClasses:true /ExceptionClass:OidcApiException /GenerateOptionalParameters:true
  /ClientClassAccessModifier:public /JsonLibrary:SystemTextJson` (`/JsonLibrary:SystemTextJson` avoids pulling
  in a Newtonsoft.Json dependency this project otherwise has no use for). This only produces typed
  request/response methods for endpoints whose success response is annotated with `[ProducesResponseType]` in
  `WalletController` - every wallet endpoint has one for exactly this reason; an endpoint without one still
  generates, but as an untyped `Task` the generated client can't deserialize a response from.
  `BusinessLogic/BiatecWalletClient.cs` wraps `Generated.OidcApiClient` behind the pre-existing
  `IBiatecWalletClient` interface (translating its DTOs into this project's own `BiatecMCP.Model` types, and
  `Generated.OidcApiException` into this project's own `WalletApiException`) so callers and their tests are
  unaffected by the swap from hand-written to generated HTTP plumbing - the generated client's methods take no
  bearer-token parameter (BiatecOIDC's OpenAPI document doesn't model per-call auth), so `BiatecWalletClient`
  sets it as the shared `HttpClient`'s `Authorization` header immediately before each call instead.
- **DEX swap aggregation**: `createSwapTransaction` fans a quote request out to three `IDexQuoteProvider`
  implementations in parallel via `DexSwapAggregatorService` (`BiatecMCP/BusinessLogic/`) -
  `BiatecRouterQuoteProvider` (the `BiatecRouterConnector` NuGet package, same one `BiatecOIDC` already
  depends on for spend valuation - `Api.QuoteAsync` for quoting, `Api.RouteTxsAsync` for building a real
  unsigned swap transaction group), `FolksRouterQuoteProvider` (raw HTTP against Folks Router's public REST
  quote endpoint, `api.folksrouter.io`), and `HaystackRouterQuoteProvider` (a deliberate no-op placeholder -
  Haystack Router's public REST contract couldn't be confirmed while building this, so it always reports "no
  quote" rather than guessing at an endpoint). A provider that throws or returns no quote is excluded from
  the comparison, never fatal. **Only Biatec Router's route can be turned into a real transaction today** -
  if Folks or Haystack quotes better, `createSwapTransaction` still returns the full comparison but no
  transaction, since those aggregators' transaction-building contracts aren't independently verified here and
  fabricating that construction risks producing a subtly wrong transaction for a real money-moving operation.
  Wiring in real transaction building for the other two is an additive follow-up once verified against a
  testnet.
- **Multisig transactions**: `MultisigTransactionBuilder` (`BiatecMCP/Helper/`) lets a multisig transaction be
  proposed once and cosigned across independent sessions - `createMultisigTransaction` derives the multisig
  account's address from `(version, threshold, participantAddresses)` (each participant identified by their
  own normal Algorand address, which already *is* their ed25519 public key, base32-encoded), builds the inner
  unsigned transaction with that address as `Sender`, and wraps it as a `SignedTransaction` envelope (the
  `Transaction` plus an empty-signature `MultisigSignature` naming every participant) - the same envelope
  every cosigner independently feeds into their own `signTransaction` call. `BiatecSelfCustodyCore`'s
  `DriveService.SignTransactionAsync` already branches on `SignedTransaction.MSig != null` and signs a
  **fresh** copy each call (not mutating in place) - exactly right for "collect N independent copies, then
  merge" - so no BiatecOIDC-side change was needed for this. `mergeMultisigTransactions` combines the
  collected copies via the Algorand4 SDK's `SignedTransaction.MergeMultisigTransactionBytes` once at least
  `threshold` of them are present, ready for `executeAlgorandTransaction`.
- **Multi-chain support**: `IAlgorandChainRegistry`/`AlgorandChainRegistry` (independently implemented in
  both `BiatecMCP/BusinessLogic/` and `BiatecOIDC/BusinessLogic/` - no shared code, per this repo's
  no-compile-time-coupling rule) discovers which Algorand-family chains are currently safe to use: it fetches
  the public [genesis list](https://scholtz.github.io/AlgorandPublicData/genesis/genesis-list.json) (skipping
  any entry with a blank `genesisHash`, e.g. a local sandbox placeholder), then for each chain fetches that
  network's [`public-algod-providers.json`](https://scholtz.github.io/AlgorandPublicData/algod/mainnet-v1.0/public-algod-providers.json)
  and calls each candidate node's own `/v2/transactions/params` (reusing the Algorand4 SDK's
  `DefaultApi.TransactionParamsAsync()` - the response shape is identical, so no bespoke JSON parsing is
  needed) - the first provider whose reported genesis hash matches the genesis list's value wins as that
  chain's live node; a chain with no currently-live matching provider is dropped entirely, never counted as
  "supported". Results are cached in-process (`IMemoryCache`, ~10 minutes - both `BiatecMCP` and `BiatecOIDC`
  avoid Redis for this, since it's cheap-to-rediscover discovery data, not session/security state).
  `IPublicAlgodDataSource`/`PublicAlgodDataSource` is a deliberate seam (the interface is what
  `AlgorandChainRegistry`'s tests mock; `PublicAlgodDataSource`, the real HTTP-calling implementation, is
  left to manual/E2E verification, same precedent as this repo's other leaf HTTP providers). In `BiatecMCP`,
  every tool's `genesisId` parameter resolves through `GetAlgodSettings` - a locally-configured
  `Algod:Networks` entry always wins first (operator control over a specific node/explorer link), falling
  back to this registry for anything else - so a chain doesn't need an `appsettings.json` entry to become
  usable, only public listing + liveness. In `BiatecOIDC`, the same registry backs a public,
  `[AllowAnonymous]` `GET /chains` (`ChainsController`) so relying parties other than `BiatecMCP` can query
  which chains this deployment currently considers usable; the response deliberately omits each node's own
  auth token/header (`AlgodApiToken`/`AlgodApiTokenHeader`), unlike `BiatecMCP`'s internal copy which needs
  them to actually call the node.
- **EVM (Ethereum-family) support**: every Biatec seed already has an Ethereum-family identity, not just an
  Algorand one - `ARC76Account.Ethereum.ARC76.GetEmailAccount(email, mnemonic, slot)` derives an
  independent secp256k1 keypair (`Nethereum.Signer.EthECKey`, already transitively available via the
  `ARC76Account.Ethereum` package - no separate Nethereum package reference needed) from the exact same
  mnemonic/email/slot `ARC76Account.Algorand.ARC76.GetEmailAccount` uses for
  Algorand - both packages expose the same method name (`GetEmailAccount`), disambiguated by namespace
  (`AlgorandArc76`/`EthereumArc76` aliases in `CloudAccountRepository.cs`), not by name (the predecessor,
  now-retired `AlgorandARC76Account` package, had them as `GetEmailAccount`/`GetEVMEmailAccount` on one
  shared type instead - see "ARC-76 package provenance" above for the full split-package migration).
  No new seed, consent flow, or storage format was needed; `CloudAccountRepository`/
  `ICloudAccountRepository` (`BiatecSelfCustodyCore`) just gained `DeriveEvmAddressAsync`, mirroring
  `DeriveAddressAsync` exactly (same `ResolveSeedEntryAsync` seed lookup) but calling
  `EthereumArc76.GetEmailAccount(...).GetPublicAddress()` instead. `WalletController` exposes this via the same
  `GET /wallet/address/{seedAddress}/{slot?}` endpoint the AVM address uses - one call derives and returns
  both - there is deliberately no per-EVM-chain concept at this layer, since one EVM address is valid
  across every EVM chain (unlike Algorand's per-`genesisId` split), so **`BiatecOIDC` has no EVM chain
  registry at all**. `POST /wallet/{network}/{address}/sign` signs EVM transactions too (see "Wallet API"
  below for the legacy/EIP-1559 request shape and `DriveService.SignEvmTransactionAsync`'s implementation) -
  scope beyond that is address + native-balance only, no broadcasting from `BiatecOIDC` itself (the caller
  submits the signed raw transaction to the chain's own RPC) and no ERC-20 balances (out of scope; see
  `BiatecMCP.cs`'s `getCryptoBalance` remarks).

  EVM signing (`DriveService.SignEvmTransactionAsync`, `BiatecSelfCustodyCore/BusinessLogic/`) is built the
  same way `SignTransactionAsync` signs Algorand transactions - resolve the seed, derive the signing key
  (`ICloudAccountRepository.LoadEvmAccountAsync`, mirroring `LoadAccountAsync` but returning a
  `Nethereum.Signer.EthECKey`), sign, discard the key. Unlike Algorand's msgpack blobs, though, an unsigned
  EVM transaction is submitted as a **field struct** (`BiatecSelfCustodyCore.Model.EvmUnsignedTransaction` -
  chainId/nonce/to/value/data/gasLimit plus either `GasPrice` for legacy or `MaxFeePerGas`+
  `MaxPriorityFeePerGas` for EIP-1559), not a raw pre-encoded byte blob - `Nethereum.Model`'s transaction
  types can only be safely built via their own field constructors (their raw-byte constructors, and
  `TransactionFactory.CreateTransaction(byte[])`, decode an already-*signed* transaction, e.g. to recover its
  sender - they don't accept an unsigned one to sign, confirmed empirically while building this). Legacy
  signs via `EthECKey.SignAndCalculateV(rawHash, chainId)` (EIP-155 - chain id is encoded into `v`); EIP-1559
  signs via `SignAndCalculateYParityV(rawHash)` (a 0/1 "y parity" byte - chain id is already a first-class
  field on the transaction itself). `WalletController.SignTransactionGroup`'s EVM branch (see "Wallet API"
  below) maps its own JSON-facing `EvmTransactionRequest` (all numeric fields as decimal/hex **strings** -
  wei-scale values routinely exceed a JSON number's safe integer range) to this struct via
  `EvmTransactionRequestParser`. No spending-limit enforcement for EVM yet (not implemented for any
  non-Algorand-mainnet chain either - see `chains.html`'s capability matrix) - `WalletService`'s new
  `SignEvmTransactionGroupAsync` skips straight to signing, no USD valuation/limit check.

  Chain-specific RPC discovery, needed only for balance queries, lives entirely in `BiatecMCP`:
  `IEvmChainRegistry`/`EvmChainRegistry` (`BiatecMCP/BusinessLogic/`) resolves EVM chains from
  [chainid.network's public chain list](https://chainid.network/chains.json) - unlike
  `IAlgorandChainRegistry`'s eager whole-list liveness check (affordable only because that list has ~7
  entries), this list has ~2,700 entries, so liveness is verified **lazily, per requested chain only**
  (`TryGetChainAsync(chainId)`/`TryGetChainByNameAsync(name)`), with a short per-chain result cache (a few
  minutes) on top of the ~10-minute raw-list cache. `IPublicEvmRpcDataSource`/`PublicEvmRpcDataSource` is the
  same interface-seam/real-HTTP-impl split as `IPublicAlgodDataSource` - raw JSON-RPC (`eth_chainId` for
  liveness, `eth_getBalance` for balance) via plain `HttpClient` POSTs, no Nethereum.Web3/RPC package needed.
  Name matching strips a trailing " Mainnet"/" One" before comparing (so "Ethereum" matches chains.json's
  "Ethereum Mainnet" and "Arbitrum" matches "Arbitrum One") - this alone covers every chain name the wallet
  needs without a hardcoded alias table.

  `INetworkResolver`/`NetworkResolver` (`BiatecMCP/BusinessLogic/`) unifies both registries behind one
  `network` string parameter: locally-configured `Algod:Networks` first (same precedence `GetAlgodSettings`
  already applies, independently reimplemented here rather than refactoring that method), then live AVM
  genesis-id/name match, then numeric EVM chain id, then EVM name match (covering the whole public list, not
  just well-known chains). Backs three new, chain-family-agnostic MCP tools: `listSupportedNetworks`
  (every live AVM chain plus four well-known EVM chains - Ethereum/Gnosis/Arbitrum/Base, the ones named when
  this was built - for discovery; other public EVM chains resolve too, just aren't listed there),
  `getCryptoAddress` (AVM family delegates straight to the existing `ResolveAlgorandAddressAsync`, EVM family
  to `ResolveEvmAddressAsync` - both now thin wrappers over one shared `ResolveDerivedAddressAsync` that
  always derives via a live BiatecOIDC `GET /wallet/address/{seedAddress}/{slot}` call (see "JWT issuer /
  OIDC provider"'s `primary_seed_address` claim note below for why there's deliberately no per-chain-family
  claim shortcut for the address itself anymore - only the *seed selector* comes from a claim), and
  `getCryptoBalance` (AVM: `DefaultApi.AccountInformationAsync` against the resolved chain's algod, same
  pattern as `CheckDestinationLiquidityAsync`, native balance + ASA holdings capped at 50; EVM:
  `IPublicEvmRpcDataSource.TryGetBalanceAsync` against the resolved chain's live RPC, native token only - the
  wei amount is carried as a decimal string, `NativeBalanceBaseUnits`, never `ulong`, since a real wei balance
  can exceed `ulong.MaxValue`).

  `BiatecOIDC/wwwroot/chains.html` (linked from `index.html`'s nav) is the "nice graphical" per-chain-family
  capability matrix: purely static HTML/CSS/JS, fetches the existing `GET /chains` client-side for the live
  AVM rows and renders a small hardcoded EVM chain list (the same four well-known ones) for the EVM rows - no
  new backend code. Per the capability rules as of this feature: every AVM chain supports address/balance/
  sign/rekey; only Algorand mainnet supports spending limits (Biatec Router isn't deployed to other AVM
  chains yet); every EVM chain supports address/balance (native token only) but not sign/limits/rekey yet
  (sending EVM transactions is planned; rekey has no EVM equivalent at all).
- **Bitcoin/Bitcoin Cash support**: two more chain families, `ChainFamily.Btc`/`ChainFamily.Bch` (added
  alongside Avm/Evm in both apps' independent `INetworkResolver` copies, recognizing the network names
  `"Bitcoin"`/`"BTC"` and `"BitcoinCash"`/`"Bitcoin Cash"`/`"BCH"`). Unlike Ethereum, there's no separate
  BIP32/BIP44 derivation path - both Bitcoin-family addresses are derived from the **exact same** secp256k1
  key `ARC76Account.Ethereum.ARC76.GetEmailAccount` already produces for EVM (`ICloudAccountRepository.LoadBitcoinKeyAsync`
  wraps it as an `NBitcoin.Key`, `BiatecSelfCustodyCore` now depends on `NBitcoin`/`NBitcoin.Altcoins`) - one
  key per seed/slot, every chain family. `DeriveBitcoinAddressAsync` formats it as Bitcoin mainnet native
  SegWit P2WPKH (`bc1...`); `DeriveBitcoinCashAddressAsync` formats the same key as legacy P2PKH under
  `NBitcoin.Altcoins.BCash`'s network, which renders as CashAddr (`bitcoincash:q...`) by default. `GET /wallet/address/{seedAddress}/{slot?}`
  derives+activates both alongside the AVM/EVM addresses in the same call (`DerivedAddressResponse` gained
  `BitcoinAddress`/`BitcoinCashAddress`).

  Being UTXO chains (not account-based like AVM/EVM), signing needs the actual inputs being spent, not just
  destination/amount - `BiatecSelfCustodyCore.Model.BitcoinUnsignedTransaction` (a plain `Inputs`/`Outputs`
  list, mirroring `EvmUnsignedTransaction`'s "fields, not a raw blob" reasoning) is the wire shape
  `POST /wallet/{network}/{address}/sign` expects for these families (`SignTransactionGroupRequest.Transactions`
  holds exactly one base64 JSON blob) - every input is assumed to be this seed/slot's own UTXO (its
  scriptPubKey is reconstructed from the derived address, never trusted from the wire), and every output
  (including change) is already explicit by the time it reaches signing - coin selection/fee estimation
  already happened on the `BiatecMCP` side. `DriveService.SignBitcoinTransactionAsync` builds the real
  `NBitcoin.Transaction` and signs via `TransactionBuilder.SignTransactionInPlace` - Bitcoin Cash's
  replay-protected SIGHASH_FORKID sighash is handled entirely by `NBitcoin.Altcoins.BCash`'s network
  (`network.CreateTransaction()` returns a `ForkIdTransaction` there), not hand-rolled - verified in this
  session via a self-signed/self-verified round trip (`DriveServiceTests`), but never against a live BCH
  node/mempool, so treat real BCH transfers as needing manual E2E verification before relying on them for
  real funds, same precedent as this repo's other leaf HTTP-dependent features.

  `BiatecMCP/BusinessLogic/BlockchairDataSource.cs` (`IPublicBitcoinDataSource`) is the one public data
  source both chains share (Blockchair's REST API shape is identical per-chain, parameterized only by the
  `{chain}` path segment - `BlockchairChainSlugs.Bitcoin`/`.BitcoinCash`) - UTXOs, confirmed balance, a
  suggested fee rate, and broadcast, all fetched fresh (no caching - balances/UTXOs change too fast to cache
  usefully, and broadcast obviously can't be). Its exact request/response shapes are documented from
  Blockchair's public API reference, not exercised against a live endpoint in this repo's build/test
  environment (no outbound network access here) - same "needs manual/E2E verification" caveat as
  `PublicAlgodDataSource`/`PublicEvmRpcDataSource`. `BiatecMCP/Helper/BitcoinTransactionBuilder.cs` is pure
  coin-selection logic (greedy largest-first, fee re-estimated as each input is added, change folded into
  the fee if it would be dust) - fully unit-tested without needing live UTXO data.

  Three chain-family-agnostic MCP tools gained Bitcoin/Bitcoin Cash branches for free (`getCryptoAddress`,
  `getCryptoBalance`, `signTransaction` - the last needs no branch at all, since it already just forwards
  whatever blob it's given to BiatecOIDC's sign endpoint); two new ones complete the transfer flow:
  `createBitcoinTransaction` (fetches UTXOs/fee rate, builds the unsigned wire DTO via
  `BitcoinTransactionBuilder`) and `executeBitcoinTransaction` (broadcasts the signed raw bytes via
  Blockchair - separate from `executeAlgorandTransaction` since the broadcast mechanism is entirely
  different, REST push vs. algod).

  Spending limits **do** apply here (unlike EVM, which has none yet) - per the same daily/weekly/monthly
  `ISpendingLimitService` buckets Algorand mainnet uses, keyed the same way (resolved `seedAddress`/`slot`).
  Since the native coin *is* the asset (no router/no asset id to quote), pricing goes through a new,
  narrower interface, `IBitcoinValuationService`/`CoinGeckoValuationService` (`BiatecOIDC`), which fetches
  both BTC-USD and BCH-USD in one call from CoinGecko's public "simple price" endpoint and caches the pair
  in Redis for `BitcoinValuation:CacheDurationMinutes` (default 5 - deliberately much shorter than
  `ExchangeRates:CacheDurationMinutes`'s 360, since a crypto spot price moves continuously rather than once
  a day). Only non-change outputs are priced (`BitcoinTransactionOutput.IsChange`) - money returning to the
  sender's own change address was never actually spent, so counting it would overstate every transfer's
  real cost against the limit.
- **Aramid bridge integration**: `createBridgeTransaction` bridges assets off Algorand via
  [Aramid Finance](https://aramid.finance), per its published AI-agent integration guide
  (`https://raw.githubusercontent.com/AramidFinance/docs/refs/heads/main/docs/developers/ai-agent-integration.md`).
  `IAramidBridgeConfigProvider`/`AramidBridgeConfigProvider` (`BiatecMCP/BusinessLogic/`) fetches Aramid's live
  bridge configuration fresh on every call (per Aramid's own "do not cache indefinitely" guidance) by finding
  the most recent Algorand mainnet transaction on Aramid's config account
  (`ARAMICOCHLHSX3G5KCKK23M72ETI537GK5VGLOVHXAGPIELWYJKIMGKK6I`) whose note starts with `aramid-config/v1:j`
  (via the Algorand4 SDK's `Algorand.Indexer.LookupApi` against a public Algonode indexer - no dedicated
  Indexer infrastructure needed), then fetching that note's IPFS hash from a public gateway. `createBridgeTransaction`
  looks up the requested route in the fetched config's `Chains`/`Chains2Tokens`, resolves the fee schedule
  active at the current round, and hands the arithmetic to `AramidBridgeCalculator` (`BiatecMCP/Helper/`,
  pure/static/fully TDD-covered) - fee amount (max of Aramid's "network floor" and "route minimum" formulas,
  always rounded up so an approximation fails closed via Aramid's own validators rejecting a mismatch, never by
  under-charging), destination-chain decimals conversion (always rounded down, per Aramid's explicit warning
  against over-crediting the destination), and the `aramid-transfer/v1:j<json>` note format/character-set
  validation. The built transaction sends to `Chains[sourceChainId].Address` (Aramid's bridge deposit address -
  never the recipient directly) for `sourceAmount + feeAmount`. Before returning, `CheckDestinationLiquidityAsync`
  verifies the bridge deposit address actually holds enough of the destination token to fulfill the
  transfer - possible only for an Algorand-family destination chain (`destinationChain.Type == "algo"`) with
  a currently-live public algod node per `IAlgorandChainRegistry` (see "Multi-chain support" below); it
  resolves that node via `TryGetChainByAramidIdAsync` and calls `AccountInformationAsync` against the bridge
  deposit address, comparing its ALGO balance/ASA holding to the computed `destinationAmount`. Insufficient
  balance **fails closed** - `ErrorType = "InsufficientDestinationLiquidity"`, no transaction returned - since
  building something that would strand a transfer is worse than refusing outright. For an EVM/NEAR
  destination, or an Algorand-family chain with no currently-live node, this can't be checked at all and the
  response instead carries `LiquidityVerified = false` plus an explanatory `Warning`, same "confirm
  independently" guidance as before. `getBridgeConfiguration` is a companion, no-auth, Algod-independent tool
  that returns every chain/route Aramid's config exposes (raw fee-alternative generations, not resolved to
  "the current one" - that still needs a live round, done by `createBridgeTransaction` itself) so an agent can
  sanity-check `destinationNetwork`/`assetId`/`destinationToken` before calling `createBridgeTransaction` at
  all. Only bridging from Algorand mainnet (`genesisId: mainnet-v1.0`, Aramid chain id `416001`) is supported.
- **JWT issuer / OIDC provider**: `JwtIssuerService` + `JwtIssuerController` (`BiatecOIDC`) implement OIDC
  discovery (`/.well-known/openid-configuration`, `/.well-known/jwks.json`), `/authorize`, `/token`, `/userinfo`,
  `/introspect`, `/verify`. Supports both standard `response_type=code` and a legacy `returnUrl` direct
  `id_token` flow. RS256 only today (PKCS#8/PKCS#1 PEM keys); EdDSA is not supported by the current
  `Microsoft.IdentityModel.Tokens` version in use. Client whitelisting and redirect URI allowlists live under
  `JwtIssuer:Clients` in `appsettings.json`; see `RedirectUriMatcher` for wildcard redirect URI matching rules and
  `OIDC_INTEGRATION_GUIDE.md` for the full integration contract. `ValidateAuthorizeRequestAsync`'s scope handling
  distinguishes two different "unexpected scope" cases: `openid` must be requested or the request fails
  (`invalid_scope`); `profile`/`email` are always granted; `sign`/`manage-limits` requested but **not**
  allowlisted in that client's `AllowedScopes` **hard-fails** the whole request with `invalid_scope` (naming
  exactly which scope(s)), since silently dropping a scope a developer explicitly asked for is more confusing
  than a clear error — but a scope this server has never heard of at all (a typo, or a literal `.default` some
  MSAL-flavored OIDC clients auto-append regardless of configuration) is silently dropped instead, since there's
  nothing to fix and failing login over library-injected noise would be worse. The actual grant is always visible
  in the token response's `scope` field.
- **Swagger UI documentation**: `BiatecOIDC/Program.cs`'s `AddSwaggerGen` call reads
  `OIDC_INTEGRATION_GUIDE.md` (copied to the output/publish directory by `BiatecOIDC.csproj`'s `<Content>`
  item, since the Dockerfile's `dotnet publish` won't otherwise include a source-tree markdown file) at
  startup and sets its full text as the generated OpenAPI document's `info.description` - Swagger UI renders
  that as markdown at the very top of the page, above the endpoint list, so anyone browsing `/swagger`
  directly gets the same integration guide developers read in the repo. Falls back to a short inline string
  if the file is somehow missing rather than failing startup.
- **Multi-address signing**: every signing identity is still, underneath, a `(seedAddress, slot)` pair —
  `seedAddress` selects *which seed* (its own identifying slot-0 address; `null`/omitted = the vault's
  current primary seed, byte-for-byte unchanged from before this existed), `slot` selects the ARC-76
  derivation index *within* that seed (default `0`). `ICloudAccountRepository.LoadAccountAsync` gained this as
  an optional trailing `seedAddress` parameter; two new read-only methods share its private
  seed-resolution helper: `DeriveAddressAsync` (derives an address without signing, backs
  `GET /wallet/address/{seedAddress}/{slot?}`) and `ResolveSeedAddressAsync` (resolves/validates a selector
  to its seed's address without deriving a slot — called once by `WalletService.SignTransactionGroupAsync`
  before pricing/limit-checking, so the resolved identity used for the spending-limit check and the identity
  actually used to sign can't disagree even if `PUT /wallet/seeds/primary` runs concurrently).
  `IDriveService.SignTransactionAsync`/`GetAccountAddressAsync` still take this pair exactly as before — but
  **no wallet endpoint accepts `seedAddress`/`slot` from a caller anymore**; see "Address-centric wallet
  API and rekey support" below for how the caller-facing surface was rebuilt entirely around the address
  itself, with `WalletController` alone doing the address → `(seedAddress, slot)` resolution before
  delegating into this unchanged layer. `GET /wallet/seeds` lists every seed's address + `isPrimary`.
- **Address-centric wallet API and rekey support**: the caller-facing wallet surface takes **the address
  itself**, not a `(seedAddress, slot)` selector — `POST /wallet/{network}/{address}/sign` and
  `GET`/`PUT /wallet/{network}/{address}/limits` (`network` a friendly chain name — `algorand`, `voi`, `base`,
  `arbitrum`, ... — resolved via a new, `BiatecOIDC`-local `INetworkResolver`/`NetworkResolver.cs`, built over
  the existing `IAlgorandChainRegistry` for AVM plus a small static well-known-name list for EVM recognition;
  a separate, independent copy from `BiatecMCP`'s own `INetworkResolver`, per this repo's no-compile-time-
  coupling rule). This is a deliberate **breaking change** from the old `seedAddress`/`slot` body
  field/query params — see `BiatecOIDC/OIDC_INTEGRATION_GUIDE.md`'s "Address-centric wallet API" section for
  the full before/after migration table. A new `IAddressActivationService`/`AddressActivationService.cs`
  (`BiatecOIDC/BusinessLogic/`) persists the address → `(seedAddress, slot)` pairing — `AddressActivationDocument`
  (`Entries: List<AddressActivationEntry>`, each `{Address, Family, SeedAddress, Slot, ActivatedUtc}`) —
  AES-encrypted on the user's **own** Drive/OneDrive (never Biatec's infrastructure), mirroring
  `SpendingLimitService`'s exact storage pattern (`EncryptedKeyRingFileStore`/`AesKeyRingResolver`/`AesOptions`
  key ring, `%AESID%`-templated filename) but in its own separate file, `AddressActivations.%AESID%.dat` — not
  the seed vault, not the spending-limit files. Every stored entry is, by construction, already verified: no
  pending/inactive tri-state. Two paths populate it: (1) automatic — `GET /wallet/address/{seedAddress}/{slot?}`
  (which derives and activates both the AVM and EVM address for that seed/slot in one call) calls
  `ActivateAsync` for each right after deriving, so the common case (any slot, including EVM) needs no
  manual step at all; (2) explicit — `POST /wallet/{network}/{seedAddress}/{slot}/activate` (`seedAddress`
  and `slot` are route segments here, not body fields; `sign` claim, body `{Address}`), the entry point for
  **rekeying an external Algorand address to a Biatec-controlled key**: if the derived address for that
  seed/slot doesn't already equal the body's `Address`, it's
  only allowed for the AVM family, and is verified on-chain (`DefaultApi.AccountInformationAsync(address).AuthAddr`
  must equal the derived address) before anything is stored — 409 `rekey_not_confirmed` if the on-chain rekey
  hasn't actually happened yet. `GET /wallet/{network}/{address}/info` reports `{Address, Network, Family,
  IsActive, SeedAddress?, Slot?}` for one address, active or not; `GET /wallet/active-addresses` reports
  every currently-active address at once (`{Addresses: [{Address, Family, SeedAddress, Slot, ActivatedUtc}, ...]}`
  — every seed's own slot-0 AVM address, whose `ActivatedUtc` is that seed's own `CreatedUtc`, concatenated
  with every entry in the registry). `WalletController.SignTransactionGroup`
  resolves `{network}/{address}` to `(seedAddress, slot)` via this registry (checking each seed's own
  primary address first, for free, before ever touching the file) before calling the unchanged
  `WalletService.SignTransactionGroupAsync` — plus a defense-in-depth `sender_mismatch` check
  (`AlgorandTransactionInspector`'s new `Sender` field, decoded from the wire `snd` key, skipped for multisig
  envelopes) that 400s if a non-multisig transaction's own `Sender` disagrees with the route's `address`.
- **Wallet API (`sign`/`manage-limits`/`rekey` scopes)**: `WalletController` (`BiatecOIDC`) exposes
  `POST /wallet/{network}/{address}/sign` (signs a transaction group via the shared `IDriveService` -
  Algorand-family *and* Ethereum-family chains, after resolving `address` — see "Address-centric wallet API
  and rekey support" above and "EVM (Ethereum-family) support" below for the two families' different request
  body shapes, sender/rekey checks, and spending-limit enforcement),
  `GET`/`PUT /wallet/limits` (the caller's own global daily/weekly/monthly spending limits and their currency —
  unchanged, no address), `GET`/`PUT /wallet/{network}/{address}/limits` (the same identity's own per-address
  bucket, replacing the old `seedAddress`/`slot` query params — see "Two-tier spending limits" below),
  `GET /wallet/limits/currencies` (every currency a limit can be set in, with its current USD rate),
  `GET /wallet/address/{seedAddress}/{slot?}` (derives both the AVM and EVM address for that seed/slot in one
  call — see "EVM (Ethereum-family) support" below — this *derives from* a seed/slot, so there's no address
  to substitute for what's being computed, and its route is unchanged), `GET /wallet/{network}/{address}/info`,
  `GET /wallet/active-addresses` (lists every currently-active address at once — see above), and
  `POST /wallet/{network}/{seedAddress}/{slot}/activate` (see above), and `GET`/`POST /wallet/seeds` +
  `PUT /wallet/seeds/primary` (the multi-seed vault — see the bullet above; `GET /wallet/seeds` also replaces
  the removed `GET /wallet/address` list endpoint).
  `POST /wallet/{network}/{address}/sign` and
  `PUT /wallet/limits`/`PUT /wallet/{network}/{address}/limits` are gated on a dedicated claim of the same name
  as the scope (`sign`/`manage-limits`), stamped onto the access token by `JwtIssuerService.CreateAccessToken`
  only when that scope was granted **and**
  the client's `AllowedScopes` allowlists it — existing clients don't get these implicitly; `GET /wallet/limits`,
  `GET /wallet/{network}/{address}/limits`, `GET /wallet/limits/currencies`, `GET /wallet/{network}/{address}/info`,
  and `GET /wallet/seeds` only require a validly authenticated caller (no
  dedicated claim, since they're read-only). `POST /wallet/{network}/{seedAddress}/{slot}/activate` requires `sign` (the
  risky on-chain action has already happened by the time it's called). `POST /wallet/{network}/{address}/sign`
  additionally requires the stricter `rekey`
  claim — gated the same allowlist way — whenever the transaction group contains a transaction with Algorand's
  `rekey` field set (a normal `sign`-scoped token is refused with 403 otherwise); this is deliberately a
  *separate*, stricter claim from `sign` because a rekey transaction permanently reassigns which key controls
  the account, unlike a payment/asset-transfer bounded by the spending limit — the consent screen shows a
  distinct danger warning when a client requests it (see `JwtIssuerController.BuildConsentHtml`'s
  `wantsRekey`/`rekeyDangerSection`). `AlgorandTransactionInspector` (`BiatecOIDC/Helper`) decodes a raw
  transaction's msgpack to find its real type/amount/asset id, its sender, and separately whether it's a rekey
  (`Transaction` subclasses' `type` property is a hardcoded per-class constant, not something decoded off the
  wire — the generic map must be peeked first; a rekey field can accompany any transaction type, independent of
  that type discriminator). Every `pay`/`axfer` in a sign request
  is priced in USD by `IAssetValuationService`/`BiatecRouterValuationService` (quoting against the Biatec Router,
  via the `BiatecRouterConnector` NuGet package's public `/quote` endpoint — mainnet USDC by default, see
  `SpendingLimitsConfiguration`), summed, converted into the caller's configured limit currency via
  `IExchangeRateService`/`CnbExchangeRateService` (Czech National Bank daily fixing, cached in Redis), and checked
  against **both** the global and the resolved `(seedAddress, slot)` identity's own per-address rolling
  daily (24h)/weekly (7d)/monthly (30d) windows (see "Two-tier spending limits" below) **before** any
  transaction in the group is signed — a group that would exceed either tier never partially signs. An asset that
  can't be priced (`AssetValuationException`) or a limit currency whose rate can't be fetched
  (`UnsupportedCurrencyException`) fails the whole request (503) rather than being silently treated as free.
  Both the limit settings and a rolling ledger of every signed `pay`/`axfer` (used to compute real trailing spend
  without re-querying the blockchain) are AES-encrypted and stored in the wallet owner's **own** cloud drive —
  not Redis, not Biatec's servers — via `SpendingLimitService`, reusing the same
  `ICloudStorageProviderCatalog`/`AesEncryptionHelper` primitives `CloudAccountRepository` uses for the account
  file itself. The provider needed to read the self-custody file and spending-limit data (`biatec_idp`) is
  stamped onto the access token at issuance, never caller-supplied, so it can't be spoofed to point at the wrong
  storage backend. `BiatecOIDC/wwwroot/index.html` (served at `/`, reachable on `oidc.biatec.io`'s own Ingress)
  is this API's documentation site.
- **Two-tier spending limits**: `SpendingLimitService` persists `SpendingLimitsDocument { Global:
  SpendingLimitSettings, PerAddress: Dictionary<string, SpendingLimitSettings> }` (key =
  `BuildAddressKey(seedAddress, slot)` = `"{seedAddress}:{slot}"`) instead of a single flat settings
  object — `ISpendingLimitService.GetLimitsAsync`/`SetLimitsAsync` take a nullable `seedAddress` selector
  (`null` = `Global`, same convention as `LoadAccountAsync`). `EnsureWithinLimitsAsync` (always called with a
  resolved, non-null identity) checks the global bucket against the *entire* ledger (unfiltered — the
  pre-split behavior, so an account that only ever configures global limits sees no change) and, if a
  per-address bucket is configured for that identity, checks it separately against ledger entries filtered to
  the same `(seedAddress, slot)` key — `SpendingLedgerEntry` gained `SeedAddress`/`Slot` fields for this
  (blank/`0` on pre-existing entries, which then only ever count toward the global tier).
  `SpendingLimitExceededException.Window` is prefixed `"global-"`/`"address-"` so callers can tell which tier
  tripped. A settings file predating this split (a flat `SpendingLimitSettings` object) is detected via a raw
  `JsonDocument` probe for a `"global"` property and migrated into `{ Global: <that>, PerAddress: {} }` on
  first read, re-saved immediately — same "migrate on read" precedent as `CloudAccountRepository`'s
  legacy-mnemonic handling. No filename/AESID change — same `SpendingLimits.%AESID%.dat`.
- **Provider access token caching**: no wallet endpoint accepts the caller's Google/Microsoft access token as a
  parameter, ever — it's always resolved by `WalletController.ResolveProviderAccessToken` from a `provider_token`
  claim cached on the bearer token itself, so the exact same Biatec token works from any device/backend, not just
  the one the user originally signed in on. `ProviderAccessTokenProtector`/`IProviderAccessTokenProtector`
  (`BiatecOIDC/BusinessLogic`) AES-256-GCM encrypts it under a **dedicated** key (`ProviderTokenProtection:Key`/`IV`
  — deliberately never `AesOptions`, so the two secrets rotate independently), captured in
  `JwtIssuerController.FinalizeAuthorizeAsync` while the ambient cookie session still has it and carried through
  `JwtIssuerService`'s issued access tokens and Redis-backed authorization-code/refresh-token records so it
  survives the code exchange and every subsequent refresh. A `refresh_token` grant has no ambient cookie session of
  its own, so instead of just carrying the cached access token forward unchanged until it expires, the caller's
  provider **refresh** token is cached the same way (`provider_refresh_token` claim,
  `ProviderAccessTokenProtector.RefreshClaimType`) and spent by `JwtIssuerService.RenewProviderTokenAsync` on every
  Biatec token refresh to mint a fresh provider access token onto the newly-issued Biatec access token —
  `WalletController` also spends it opportunistically, mid-lifetime of a still-valid Biatec token, if a wallet call
  hits a stale cached access token (`UnauthorizedAccessException`), retrying once. Only when there's no cached
  provider refresh token at all, or the provider rejects it (revoked/expired), does the caller fall back to needing
  a fresh interactive `/authorize` sign-in — there is no parameter to work around this with. This is a deliberate,
  security-sensitive trade-off — the whole point is that a relying party
  only ever needs to hold a Biatec token, never the user's own Google/Microsoft token, which does widen blast
  radius if this service is ever compromised; see `OIDC_INTEGRATION_GUIDE.md`'s "Provider access token caching"
  section for the full threat-model writeup and why a dedicated key (rather than reusing `AesOptions`) and
  client-embedded caching (rather than a server-side lookup table) were chosen specifically to bound that risk.
  Because there's no caller-supplied fallback, `ProviderAccessTokenProtector`'s constructor fails fast (throws
  `InvalidOperationException`, same precedent as `JwtIssuerService.LoadOrCreateSigningKey`) outside `Development`
  if the dedicated key is missing/invalid — a misconfigured key means the wallet API can't function at all, so
  that's surfaced immediately rather than as a wall of unexplained 401s.
## Conventions and constraints

- Interfaces are prefixed `I` and registered as `Scoped` in each project's `Program.cs`. `GoogleCloudStorageProvider`,
  `MicrosoftCloudStorageProvider`, and `CrossAccountProtectionService` are typed `HttpClient`s (registered via
  `AddHttpClient<T>()`) exposed to callers only through a `Scoped` interface registration
  (`AddScoped<ICloudStorageProvider>(sp => sp.GetRequiredService<T>())`). Follow this pattern for new services —
  do not root-resolve a `Scoped` service from `app.Services` (unsafe under `ValidateScopes`); framework
  `Singleton`s like `IActionDescriptorCollectionProvider` are the one thing safe to touch that way (see the
  startup warm-up below).
- **Startup warm-up (do not remove on a `Program.cs` rewrite)**: both apps force ASP.NET Core to eagerly compile
  the full endpoint/route model right before `app.Run()`, by enumerating `((IEndpointRouteBuilder)app).DataSources`
  and touching each `.Endpoints`. `BiatecOIDC` (which has MVC controllers) also warms up
  `IActionDescriptorCollectionProvider.ActionDescriptors`; `BiatecMCP` does not — it has no controllers left
  (`MapMcp`/`MapGet` are Minimal API endpoints, fully covered by the `DataSources` warm-up alone) and
  `IActionDescriptorCollectionProvider` isn't even registered there, so calling it would throw. This exists so a
  pod that just passed its Kubernetes readiness probe doesn't make whichever real user request lands first pay for
  assembly scanning and route-table construction that has nothing to do with that request.
- Configuration is strongly typed via `IOptions<T>` bound from named sections in `appsettings.json` — add new
  settings as a new `Model/*.cs` POCO + `builder.Services.Configure<T>(...)` rather than reading `IConfiguration`
  directly in business logic. Only add a type to `BiatecSelfCustodyCore` if both services genuinely need it;
  otherwise keep it local to `BiatecMCP` or `BiatecOIDC`.
- Never log or persist decrypted private key material. Treat `AesEncryptionHelper` and anything touching
  `StorageFileName`/private keys as security-sensitive; changes there warrant extra scrutiny.
- Redirect URI validation for OIDC (`/authorize`) must remain an allowlist check against `JwtIssuer:Clients` —
  do not loosen this to permissive matching without explicit instruction.
- When bumping the target .NET version, update it everywhere, not just the `.csproj` files: base images in both
  `BiatecMCP/Dockerfile` and `BiatecOIDC/Dockerfile` (`mcr.microsoft.com/dotnet/aspnet:<ver>` and
  `mcr.microsoft.com/dotnet/sdk:<ver>`), the version mentioned in this file, `.github/copilot-instructions.md`, and
  `BiatecMCP/README.md`. A `TargetFramework` bump alone will build fine locally but ship a Docker image on the old
  runtime.
- This is proprietary software (Scholtz & Company, j.s.a.) — do not add third-party license headers or open-source
  boilerplate.
- Three markdown docs carry deep protocol context and should be kept accurate when touching these areas:
  `BiatecOIDC/OIDC_INTEGRATION_GUIDE.md`, `BiatecOIDC/BIATEC_OIDC_LOGOUT_REQUIREMENTS.md`, and
  `BiatecOIDC/ENTRA_SETUP_GUIDE.md`.

## Skills

- `biatec-oidc-jwt` (`.claude/skills/biatec-oidc-jwt/SKILL.md`) — condensed reference for the OIDC/JWT issuer
  (endpoints, claims, redirect-URI/logout allowlist rules, signing-key format), the wallet API's
  daily/weekly/monthly spending-limit enforcement (Biatec Router USD valuation, Czech National Bank FX rates,
  encrypted cloud-drive storage), and its encrypted provider-access-token caching. Use this instead of reading
  the two full guide docs above when working on `/authorize`, `/token`, `/register`, `/userinfo`, `/introspect`,
  `/verify`, `/connect/endsession`, `/logout`, `/wallet/{network}/{address}/sign`, `/wallet/limits`,
  `/wallet/{network}/{address}/limits`, `/wallet/limits/currencies`, `/wallet/{network}/{address}/info`,
  `/wallet/{network}/{seedAddress}/{slot}/activate`,
  `JwtIssuerService.cs`, `JwtIssuerController.cs`, `WalletController.cs`, `WalletService.cs`,
  `SpendingLimitService.cs`, `AddressActivationService.cs`, `NetworkResolver.cs`,
  `ProviderAccessTokenProtector.cs`, `BiatecRouterValuationService.cs`,
  `CnbExchangeRateService.cs`, `RedirectUriMatcher.cs`, or
  `JwtIssuer:*`/`SpendingLimits:*`/`ExchangeRates:*`/`ProviderTokenProtection:*` config (all in `BiatecOIDC/`).
