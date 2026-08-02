# CLAUDE.md

This file guides Claude Code when working in this repository. It must stay in sync with
[.github/copilot-instructions.md](.github/copilot-instructions.md) — the two files serve the same purpose for
different AI assistants (Claude Code vs. GitHub Copilot). Whenever you update one, update the other to match.

## Project overview

Biatec — two independently deployed ASP.NET Core 10 services:

- **BiatecOIDC** is an OpenID Connect identity provider (JWT issuer) *and* a self-custody wallet API: whitelisted
  (or, for MCP-class clients, dynamically self-registered — see below) third-party apps authenticate users via
  Google or Microsoft Entra ID and receive Algorand-identity claims, and can sign Algorand transactions on the
  user's behalf via `POST /wallet/sign` (spend-limit-enforced, never handing out the user's own key material).
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
  - `MCP/BiatecMCP.cs` — 10 MCP tools split into three chainable steps (build → sign → execute) rather than
    one monolithic call, so an unsigned transaction can be inspected, handed to a different signer, or
    combined as part of a multisig proposal before ever being broadcast: `getAlgorandAddress`,
    `listAlgorandAddresses` (read-only); `createPaymentTransaction`, `createOptInTransaction`,
    `createAssetCreateTransaction`, `createSwapTransaction`, `createBridgeTransaction` (**architecture
    placeholder** for a future Aramid Finance bridge - always returns a "not implemented" error today),
    `createMultisigTransaction` (build-only, no `sign` claim needed - see "Multisig transactions" below);
    `signTransaction` (new, standalone - forwards to BiatecOIDC's `POST /wallet/sign`, requires `sign`),
    `mergeMultisigTransactions` (combines independently-signed multisig copies, no BiatecOIDC/Algod call);
    `executeAlgorandTransaction` (broadcasts already-signed transactions, requires `sign`). Every tool's
    `[Description]` names the next tool in the chain, since MCP has no other side-channel for teaching the
    connected agent the intended protocol. All forward the caller's own bearer token to BiatecOIDC rather
    than touching any key material - see "MCP server" under Architecture notes below for the full request
    flow. The `create*`/`getAlgorandAddress` tools accept optional `primaryAddress`/`slot` parameters to
    build against/from a specific seed/ARC-76 slot instead of the default identity (see BiatecOIDC's
    "Multi-address signing" note).
  - `BusinessLogic/IBiatecWalletClient.cs` + `BiatecWalletClient.cs` — typed `HttpClient` wrapping BiatecOIDC's
    `POST /wallet/sign`/`GET /wallet/seeds`/`GET /wallet/address`/`GET /wallet/address/{primaryAddress}/{slot}`,
    forwarding the caller's bearer token; `WalletApiException` carries BiatecOIDC's `ProblemDetails` title/detail
    back to the tool
  - `BusinessLogic/IDexQuoteProvider.cs` + `BiatecRouterQuoteProvider.cs`/`FolksRouterQuoteProvider.cs`/
    `HaystackRouterQuoteProvider.cs` + `DexSwapAggregatorService.cs` — `createSwapTransaction`'s quote
    comparison (see "DEX swap aggregation" under Architecture notes below for the scope decision on which
    provider can actually build a transaction today)
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
  - `wwwroot/` — static pages: `index.html`, `privacy.html`, `terms.html`
- `BiatecOIDC/` — the OIDC/JWT issuer web/API project (net10.0, `Microsoft.NET.Sdk.Web`)
  - `Controllers/JwtIssuerController.cs` — `/authorize` (+ `idp` fast track, + `resource` for RFC 8707), `/token`
    (+ `resource`), `/register` (RFC 7591 Dynamic Client Registration — public clients only, no secret ever
    issued; see "MCP server" under Architecture notes for why this exists), `/select-provider` (picker page,
    one button per provider registered in the catalog), `/authorize/challenge`, `/authorize/callback` (verifies
    storage-write access via `catalog.Resolve(idp).HasWriteAccessAsync(...)` before finalizing)
  - `Controllers/WalletController.cs` — `/wallet/sign` (`sign` claim), `/wallet/limits` get (identity only)/put
    (`manage-limits` claim), `/wallet/limits/currencies` (identity only); same manual bearer-token pattern as
    `JwtIssuerController`'s `/userinfo` (not `[Authorize]` — see `.claude/skills/biatec-oidc-jwt/SKILL.md`)
  - `BusinessLogic/JwtIssuerService.cs` (+ `IJwtIssuerService`) — depends on `BiatecSelfCustodyCore`'s
    `IDriveService` for the `algorand_address` claim; also stamps `biatec_idp`/`sign`/`manage-limits` claims
    onto issued access tokens. `ResolveClientAsync(clientId)` checks statically-configured `JwtIssuer:Clients`
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
  - `BusinessLogic/ProviderAccessTokenProtector.cs` (+ `IProviderAccessTokenProtector`) — AES-256-GCM encrypts the
    caller's Google/Microsoft access token (under `ProviderTokenProtectionConfiguration`, a key dedicated to this
    - never `AesOptions`) so it can be cached inside issued access/refresh tokens; see the "Provider access token
    caching" architecture note below
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
- **Multi-seed vault and on-chain rekey**: the account file's decrypted content is a `SeedVault`
  (`BiatecSelfCustodyCore.Model`) — a list of independently-generated `SeedVaultEntry` seeds, each identified
  by its own ARC-76 slot-0 address (`PrimaryAddress`), with exactly one flagged `IsPrimary` at a time.
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
  through the existing `POST /wallet/sign`, which is what actually enforces the `rekey` claim: see the
  "Wallet API" bullet below and `AlgorandTransactionInspector`'s `IsRekey` detection. Only once that
  transaction is confirmed on-chain should the caller call `PUT /wallet/seeds/primary` — switching primary
  before that would make Biatec sign with a key the account no longer recognizes.
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
  to BiatecOIDC's wallet REST API (`IBiatecWalletClient`/`BiatecWalletClient`) — `getAlgorandAddress` (with
  no `slot`/`primaryAddress` given) reads the token's own `algorand_address` claim (falling back to
  `GET /wallet/seeds`'s primary seed); any non-default identity, or `listAlgorandAddresses`, hits
  `GET /wallet/address`/`GET /wallet/address/{primaryAddress}/{slot?}` instead. Wallet operations are three
  separate, chainable tools rather than one monolithic call: `createPaymentTransaction`/
  `createOptInTransaction`/`createAssetCreateTransaction`/`createSwapTransaction`/`createMultisigTransaction`
  build an *unsigned* transaction locally (`AlgorandTransactionBuilder`/`MultisigTransactionBuilder`,
  Algorand4 SDK, no key material touched, no `sign` claim required - building a proposal is harmless);
  `signTransaction` forwards it to `POST /wallet/sign` — forwarding `primaryAddress`/`slot` if given —
  (BiatecOIDC enforces the `sign`/`rekey` claim and both spending-limit tiers there —
  `McpTransferLimitsConfiguration`/`TransferPolicy` were removed from `BiatecMCP` entirely, since
  `/wallet/sign` already does this uniformly for every relying party) and requires the `sign` claim itself;
  `executeAlgorandTransaction` broadcasts the signed bytes to Algod via a shared private
  `SubmitSignedTransactionsAsync` helper, also requiring `sign`. `mergeMultisigTransactions` combines
  independently-signed copies of a `createMultisigTransaction` envelope (no BiatecOIDC/Algod call - pure
  local combination via the Algorand4 SDK). `createBridgeTransaction` is an architecture placeholder for a
  future Aramid Finance bridge integration, always returning a "not implemented" error today. Every tool's
  `[Description]` names the next tool in the intended chain, since MCP has no other side-channel for this.
  Mounted at `/mcp` via `ModelContextProtocol.AspNetCore`
  (`AddMcpServer().WithHttpTransport().WithToolsFromAssembly().AddAuthorizationFilters()`), stateless HTTP
  transport.
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
- **Multi-address signing**: every signing identity is a `(primaryAddress, slot)` pair — `primaryAddress`
  selects *which seed* (its own identifying slot-0 address; `null`/omitted = the vault's current primary seed,
  byte-for-byte unchanged from before this existed), `slot` selects the ARC-76 derivation index *within* that
  seed (default `0`). `ICloudAccountRepository.LoadAccountAsync` gained this as an optional trailing
  `primaryAddress` parameter; two new read-only methods share its private seed-resolution helper:
  `DeriveAddressAsync` (derives an address without signing, backs `GET /wallet/address/{primaryAddress}/{slot?}`)
  and `ResolveSeedAddressAsync` (resolves/validates a selector to its seed's address without deriving a slot —
  called once by `WalletService.SignTransactionGroupAsync` before pricing/limit-checking, so the resolved
  identity used for the spending-limit check and the identity actually used to sign can't disagree even if
  `PUT /wallet/seeds/primary` runs concurrently). `IDriveService.SignTransactionAsync`/`GetAccountAddressAsync`
  and `POST /wallet/sign`'s request body (`PrimaryAddress`/`Slot`, both optional) forward the same selector
  straight through. `GET /wallet/address` lists every seed's address + `isPrimary` (same data as
  `GET /wallet/seeds`, addressed for this use case).
- **Wallet API (`sign`/`manage-limits`/`rekey` scopes)**: `WalletController` (`BiatecOIDC`) exposes
  `POST /wallet/sign` (signs an Algorand transaction group via the shared `IDriveService`), `GET`/`PUT /wallet/limits`
  (the caller's own daily/weekly/monthly spending limits and their currency — global by default, or a specific
  `(primaryAddress, slot)`'s own bucket via optional `primaryAddress`/`slot` query params — see "Multi-address
  signing" above and "Two-tier spending limits" below), `GET /wallet/limits/currencies`
  (every currency a limit can be set in, with its current USD rate), `GET /wallet/address` +
  `GET /wallet/address/{primaryAddress}/{slot?}`, and `GET`/`POST /wallet/seeds` +
  `PUT /wallet/seeds/primary` (the multi-seed vault — see the bullet above). `POST /wallet/sign` and
  `PUT /wallet/limits` are gated on a dedicated claim of the same name as the scope (`sign`/`manage-limits`),
  stamped onto the access token by `JwtIssuerService.CreateAccessToken` only when that scope was granted **and**
  the client's `AllowedScopes` allowlists it — existing clients don't get these implicitly; `GET /wallet/limits`,
  `GET /wallet/limits/currencies`, and `GET /wallet/seeds` only require a validly authenticated caller (no
  dedicated claim, since they're read-only). `POST /wallet/sign` additionally requires the stricter `rekey`
  claim — gated the same allowlist way — whenever the transaction group contains a transaction with Algorand's
  `rekey` field set (a normal `sign`-scoped token is refused with 403 otherwise); this is deliberately a
  *separate*, stricter claim from `sign` because a rekey transaction permanently reassigns which key controls
  the account, unlike a payment/asset-transfer bounded by the spending limit — the consent screen shows a
  distinct danger warning when a client requests it (see `JwtIssuerController.BuildConsentHtml`'s
  `wantsRekey`/`rekeyDangerSection`). `AlgorandTransactionInspector` (`BiatecOIDC/Helper`) decodes a raw
  transaction's msgpack to find its real type/amount/asset id, and separately whether it's a rekey
  (`Transaction` subclasses' `type` property is a hardcoded per-class constant, not something decoded off the
  wire — the generic map must be peeked first; a rekey field can accompany any transaction type, independent of
  that type discriminator). Every `pay`/`axfer` in a sign request
  is priced in USD by `IAssetValuationService`/`BiatecRouterValuationService` (quoting against the Biatec Router,
  via the `BiatecRouterConnector` NuGet package's public `/quote` endpoint — mainnet USDC by default, see
  `SpendingLimitsConfiguration`), summed, converted into the caller's configured limit currency via
  `IExchangeRateService`/`CnbExchangeRateService` (Czech National Bank daily fixing, cached in Redis), and checked
  against **both** the global and the resolved `(primaryAddress, slot)` identity's own per-address rolling
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
  `BuildAddressKey(primaryAddress, slot)` = `"{primaryAddress}:{slot}"`) instead of a single flat settings
  object — `ISpendingLimitService.GetLimitsAsync`/`SetLimitsAsync` take a nullable `primaryAddress` selector
  (`null` = `Global`, same convention as `LoadAccountAsync`). `EnsureWithinLimitsAsync` (always called with a
  resolved, non-null identity) checks the global bucket against the *entire* ledger (unfiltered — the
  pre-split behavior, so an account that only ever configures global limits sees no change) and, if a
  per-address bucket is configured for that identity, checks it separately against ledger entries filtered to
  the same `(primaryAddress, slot)` key — `SpendingLedgerEntry` gained `PrimaryAddress`/`Slot` fields for this
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
  `/verify`, `/connect/endsession`, `/logout`, `/wallet/sign`, `/wallet/limits`, `/wallet/limits/currencies`,
  `JwtIssuerService.cs`, `JwtIssuerController.cs`, `WalletController.cs`, `WalletService.cs`,
  `SpendingLimitService.cs`, `ProviderAccessTokenProtector.cs`, `BiatecRouterValuationService.cs`,
  `CnbExchangeRateService.cs`, `RedirectUriMatcher.cs`, or
  `JwtIssuer:*`/`SpendingLimits:*`/`ExchangeRates:*`/`ProviderTokenProtection:*` config (all in `BiatecOIDC/`).
