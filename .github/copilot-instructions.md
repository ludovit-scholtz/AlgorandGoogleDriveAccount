# GitHub Copilot instructions

This file must stay in sync with [CLAUDE.md](../CLAUDE.md) at the repo root — the two files carry the same
project knowledge for different AI assistants (GitHub Copilot vs. Claude Code). Whenever you update one, update
the other to match.

## Project overview

Biatec — two independently deployed ASP.NET Core 10 services that used to be one app and were split apart:

- **BiatecMCP** gives AI assistants (via the Model Context Protocol) self-custody access to Algorand accounts.
  Private keys are AES-256 encrypted, bound to the user's email address, and stored only in the user's own Google
  Drive or OneDrive (user's choice) — never on Biatec's servers.
- **BiatecOIDC** is an OpenID Connect identity provider (JWT issuer) so whitelisted third-party apps can
  authenticate users via Google or Microsoft Entra ID and receive Algorand-identity claims.

Both apps support **two identity/storage providers** — Google (Drive) and Microsoft Entra ID (OneDrive app
folder) — presented as a picker (or skippable via `?idp=google`/`?idp=microsoft`, the "fast track") wherever a
user signs in: `pair.html`'s two buttons in `BiatecMCP`, and `BiatecOIDC`'s `/select-provider` page. See
`BiatecOIDC/ENTRA_SETUP_GUIDE.md` for the Entra app registration and `Microsoft Graph Files.ReadWrite.AppFolder`
permission this depends on.

`BiatecMCP` is served at `https://google.biatec.io` (the MCP endpoint stays at
`https://google.biatec.io/mcp/`). `BiatecOIDC` has its own dedicated domain, `https://oidc.biatec.io` (the
recommended host for new integrations), and remains reachable via a carved-out set of paths on
`https://google.biatec.io` too, as a legacy alias for existing integrations (see "Kubernetes / ingress routing"
below) — both hosts are internally self-consistent since `JwtIssuerService.GetIssuer` derives the `iss`
claim/discovery `issuer` from the actual request host rather than a hardcoded value. The two services share one
piece of self-custody infrastructure, `BiatecSelfCustodyCore` (see below), because the OIDC provider embeds an
`algorand_address` claim in issued tokens, which requires reading the same Drive/OneDrive-backed account BiatecMCP
manages.

## Solution layout

- `BiatecSelfCustodyCore/` — shared class library (net10.0, `Microsoft.NET.Sdk`), referenced by both `BiatecMCP`
  and `BiatecOIDC`. Holds the security-sensitive self-custody code so it exists in exactly one place:
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
- `BiatecMCP/` — the MCP server + self-custody web/API project (net10.0, `Microsoft.NET.Sdk.Web`)
  - `Controllers/` — `DevicePairingController` (provider-aware: `pair-device?idp=`, `GET providers` for the picker
    UI, `RequestStorageAccess`, `StorageAccessCallback`), `DriveController`
  - `BusinessLogic/` — `DevicePairingService`, `CrossAccountProtectionService`, `PortfolioValuationService`
    (+ their `I*Service` interfaces)
  - `Model/` — `DevicePairingModels` (`PairedDeviceInfo.Provider`), `McpTransferLimitsConfiguration`,
    `AlgodConfiguration`, `CrossAccountProtectionConfiguration`, plus local `RedisConfiguration`/`CorsConfiguration`
    copies
  - `MCP/BiatecMCPGoogle.cs` — MCP tool definitions exposed to AI clients (e.g. `getAlgorandAddress`)
  - `Helper/` — `SecureTokenGenerator`, `TransferPolicy`
  - `wwwroot/` — static pages: `index.html`, `pair.html` (device pairing UI; provider buttons rendered from
    `GET /api/device/providers`, not hardcoded), `privacy.html`, `terms.html`
- `BiatecOIDC/` — the OIDC/JWT issuer web/API project (net10.0, `Microsoft.NET.Sdk.Web`)
  - `Controllers/JwtIssuerController.cs` — `/authorize` (+ `idp` fast track), `/select-provider` (picker page,
    one button per provider registered in the catalog), `/authorize/challenge`, `/authorize/callback` (verifies
    storage-write access via `catalog.Resolve(idp).HasWriteAccessAsync(...)` before finalizing)
  - `Controllers/WalletController.cs` — `/wallet/sign` (`sign` claim), `/wallet/limits` get (identity only)/put
    (`manage-limits` claim), `/wallet/limits/currencies` (identity only); same manual bearer-token pattern as
    `JwtIssuerController`'s `/userinfo` (not `[Authorize]` — see `.claude/skills/biatec-oidc-jwt/SKILL.md`)
  - `BusinessLogic/JwtIssuerService.cs` (+ `IJwtIssuerService`) — depends on `BiatecSelfCustodyCore`'s
    `IDriveService` for the `algorand_address` claim; also stamps `biatec_idp`/`sign`/`manage-limits` claims
    onto issued access tokens
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
- `BiatecMCPTests/` — NUnit + Moq tests for `BiatecMCP` + `BiatecSelfCustodyCore` (device pairing, Drive
  controller, AES encryption, transfer policy, `CloudStorageProviderCatalog`, `GoogleCloudStorageProvider`,
  `MicrosoftCloudStorageProvider`, plus a shared `FakeCloudStorageProvider` test double)
- `BiatecOIDCTests/` — NUnit + Moq tests for `BiatecOIDC` (JWT issuer service + controller)

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

Both services require Redis (`Redis:ConnectionString` in their respective `appsettings.json`), Google OAuth 2.0
credentials (`CloudServices:Google:ClientId`/`ClientSecret`), and Microsoft Entra ID credentials
(`CloudServices:Entra:TenantId`/`ClientId`/`ClientSecret` — see `BiatecOIDC/ENTRA_SETUP_GUIDE.md`) to run.

CI is two separate GitHub Actions workflows, not one — nothing pushed to `master` reaches production
automatically. `.github/workflows/deploy-stage.yml` builds/pushes both Docker images and deploys them
straight to the **stage** environment on every push to `master`. `.github/workflows/promote-production.yml`
is manually triggered (`workflow_dispatch`) and re-deploys an already-built, already-stage-tested image tag
to **production** — it never rebuilds anything. See [docs/STAGE_ENVIRONMENT.md](../docs/STAGE_ENVIRONMENT.md)
for the full stage/production architecture and [docs/CICD_GITHUB_ACTIONS.md](../docs/CICD_GITHUB_ACTIONS.md)
for the required GitHub secrets (shared by both workflows) and
[docs/KUBE_CONFIG_SECURITY.md](../docs/KUBE_CONFIG_SECURITY.md) for why the CI kubeconfig is namespace-scoped
and short-lived. There is no automated test job in CI, so run tests locally before pushing.

Root `.editorconfig` + `Directory.Build.props` (auto-imported by all 5 `.csproj`s) enable the built-in Roslyn
analyzers solution-wide and promote `IDE0005` (unused usings) to a build error. Run `dotnet format Biatec.slnx` to
fix flagged style/unused-using issues, and `dotnet format Biatec.slnx --verify-no-changes` before committing —
both should be clean.

## Kubernetes / ingress routing

Both services run as separate Deployments/Services in the `biatec` namespace. `BiatecMCP` owns the
`google.biatec.io` host outright; `BiatecOIDC` is reachable on its own dedicated `oidc.biatec.io` host **and** via
a carved-out set of paths on `google.biatec.io` (a legacy alias, kept working for integrations set up before
`oidc.biatec.io` existed) — three Ingress objects total across the two deployment manifests:

- `k8s/main/deployment-mcp.yaml` — `biatec-mcp-app-deployment`/`biatec-mcp-service`/`biatec-mcp-ingress`.
  Catch-all path (`/(.*)`, `rewrite-target: /$1`) on `google.biatec.io` — this is the default backend for the
  host, so `/mcp`, `/api/drive`, `/api/device`, `/`, and all static `wwwroot` pages keep resolving here unchanged.
  Any Ingress using this regex-catch-all idiom (this one, `biatec-oidc-domain-ingress`, and both `k8s/stage/*`
  Ingresses) needs **both** `nginx.ingress.kubernetes.io/use-regex: "true"` **and**
  `pathType: ImplementationSpecific` on that path — `pathType: Prefix` means a literal path-segment match per
  the Ingress spec, so ingress-nginx's admission webhook rejects a regex path there even with `use-regex` set
  (`path /(.*) cannot be used with pathType Prefix`). The literal/`Exact`-path `biatec-oidc-ingress` below needs
  neither, since none of its paths are regexes.
- `k8s/main/deployment-oidc.yaml` — two Ingress objects for `biatec-oidc-app-deployment`/`biatec-oidc-service`:
  - `biatec-oidc-ingress` — claims only the OIDC-specific literal paths on the shared `google.biatec.io` host
    (`/.well-known`, `/authorize`, `/token`, `/userinfo`, `/introspect`, `/verify`, `/connect/endsession`,
    `/logout`, `/select-provider`, `/oidc/signin-google`, `/oidc/signin-microsoft`), no rewrite. nginx-ingress
    matches literal/prefix locations ahead of `biatec-mcp-ingress`'s regex catch-all regardless of object order,
    so this reliably carves out just those paths without touching anything else on that host.
  - `biatec-oidc-domain-ingress` — the whole `oidc.biatec.io` host, full catch-all (`/(.*)`, `rewrite-target:
    /$1`, same idiom as `biatec-mcp-ingress`) straight to `biatec-oidc-service`, with its own TLS entry/secret
    (`tls-oidc.biatec.io`). Kept as a separate Ingress object rather than an extra host block on
    `biatec-oidc-ingress`, because the `rewrite-target` annotation a catch-all needs applies Ingress-object-wide
    and would otherwise also change how that Ingress's literal/`Exact` paths for `google.biatec.io` are matched.

  `BiatecOIDC`'s Google **and** Microsoft OIDC handlers use non-default `CallbackPath`s (`/oidc/signin-google`,
  `/oidc/signin-microsoft`) specifically so they land on this deployment and not on `BiatecMCP`'s catch-all
  (which can't decrypt this app's correlation cookie — separate processes, no shared Data Protection key ring).
  Both callback paths work on both hosts (`google.biatec.io` and `oidc.biatec.io`) since they're just paths, not
  host-specific — but each is a *distinct redirect URI* as far as Google/Entra's own app-registration allowlists
  are concerned, so adding `oidc.biatec.io` as a new host means also adding
  `https://oidc.biatec.io/oidc/signin-google` (Google Cloud Console OAuth client) and
  `https://oidc.biatec.io/oidc/signin-microsoft` (Entra app registration, see `BiatecOIDC/ENTRA_SETUP_GUIDE.md`)
  there — external, one-time, manual steps outside this repo. `BiatecMCP` keeps the framework's default
  `/signin-google` and a `/signin-microsoft` CallbackPath, both fine as-is since its ingress is the catch-all.

  Neither host hardcodes `JwtIssuer:Issuer` in `k8s/main/conf-oidc/appsettings.json` — deliberately: leaving it
  unset means `JwtIssuerService.GetIssuer` derives the `iss` claim/discovery `issuer` from whichever host
  actually received the request, so `oidc.biatec.io` and the `google.biatec.io` alias each stay internally
  self-consistent. Setting a static `Issuer` there would fix `iss` to one value and break discovery on whichever
  host *isn't* that value (its `/.well-known/openid-configuration` would advertise an `issuer` that doesn't match
  the host it was fetched from — a mismatch strict OIDC clients reject). Do not add a static `Issuer` to that
  ConfigMap without re-checking this reasoning.

Both deployments reuse the same secrets (`google-account-main-app-secret` for app config,
`csharp-cert`/`csharp-cert-password` for the internal Kestrel HTTPS cert) — there was no need to provision new
ones. Config is split per-service: `k8s/main/conf-mcp/` / `biatec-mcp-conf` and `k8s/main/conf-oidc/` /
`biatec-oidc-conf`.

## Stage environment

`k8s/stage/` mirrors `k8s/main/` for both services, at `stage.google.biatec.io` /
`stage.oidc.biatec.io`, in the **same `biatec` namespace** with `-stage`-suffixed resource names
(not a separate namespace — the existing namespace-scoped CI `Role` grants verbs on resource
*types*, so stage needed no new RBAC). `deploy-stage.yml` deploys here on every push to `master`;
`k8s/main/*` (production) only changes via the manually-triggered `promote-production.yml`. Stage
uses its own dedicated Kubernetes Secret, `biatec-stage-app-secret` — never production's
`google-account-main-app-secret` — generated once via
[k8s/stage/generate-stage-secret.sh](../k8s/stage/generate-stage-secret.sh), which always mints a
fresh AES key and JWT signing key dedicated to stage (never copied from production). Self-custody
files are further isolated on top of that by `App:StorageFolderName` being `"BiatecStage"` there
(vs `"Biatec"` in production), set directly in the stage ConfigMaps. See
[docs/STAGE_ENVIRONMENT.md](../docs/STAGE_ENVIRONMENT.md) for the full picture, including what is
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
- **Pluggable cloud storage providers**: `ICloudStorageProvider` (`BiatecSelfCustodyCore/Providers/`) is the
  single extension point for a new storage backend — implement it, register it in DI, done; `ICloudAccountRepository`,
  `ICloudStorageProviderCatalog`, and both picker UIs (`BiatecMCP`'s `GET /api/device/providers`, `BiatecOIDC`'s
  `/select-provider`) all resolve providers dynamically and need zero code changes for provider #3+. To add one:
  implement `ICloudStorageProvider` (including `DeleteAsync` — best-effort, never throws, used only to clean up
  a file just migrated to a new AES key generation), register it in **both** `BiatecMCP/Program.cs` and
  `BiatecOIDC/Program.cs` the same way as the existing Google/Microsoft blocks (`AddHttpClient<T>()` +
  `AddScoped<ICloudStorageProvider>(sp => sp.GetRequiredService<T>())`), then add a matching authentication
  scheme block (copy the Microsoft `AddOpenIdConnect(...)` block as a template) whose `OnTokenValidated` calls
  `CloudStorageProviderClaims.Stamp(context.Principal, YourProvider.ProviderName)`.
- **Multi-provider auth**: both `BiatecMCP` and `BiatecOIDC` independently configure two authentication schemes —
  Google via `Google.Apis.Auth.AspNetCore3` (`AddGoogleOpenIdConnect`) and Microsoft Entra ID via the plain
  `AddOpenIdConnect(MicrosoftCloudStorageProvider.ProviderName, ...)` handler pointed at
  `https://login.microsoftonline.com/{TenantId}/v2.0` — both sign into the same cookie scheme, so `[Authorize]`
  endpoints don't care which provider was used. Google scopes: `openid profile email` +
  `DriveService.Scope.DriveFile`. Microsoft scopes: `openid profile email offline_access` +
  `https://graph.microsoft.com/Files.ReadWrite.AppFolder`. Each scheme's `OnTokenValidated` stamps a `biatec_idp`
  claim (`AuthSchemeNames.IdpClaimType`, via `CloudStorageProviderClaims.Stamp`) onto the signed-in principal so
  later code knows which storage backend to use. See `BiatecOIDC/ENTRA_SETUP_GUIDE.md` for the Entra app
  registration this depends on. Cross-Account Protection (Google RISC) lives only in `BiatecMCP` and is supported
  but disabled by default (`CrossAccountProtection:Enabled`).
- **Provider picker / fast track**: a user chooses Google or Microsoft via `pair.html`'s dynamically-rendered
  buttons (`BiatecMCP`) or `BiatecOIDC`'s `/select-provider` page (also dynamically rendered, one button per
  provider in the catalog); either can be skipped with `?idp=google`/`?idp=microsoft` on `/api/device/pair-device`
  or `/authorize`. Before finalizing either flow, `catalog.Resolve(idp).HasWriteAccessAsync(accessToken)`
  confirms the fresh token actually has storage-write access (declining just that consent checkbox is possible
  even while completing sign-in); if missing, the browser is sent through one incremental-consent round-trip
  (forced fresh consent screen, `OpenIdConnectIncrementalAuth`) before the pairing/OIDC code is finalized, capped
  at one retry to avoid loops.
- **Device pairing**: `DevicePairingService`/`DevicePairingController` (`BiatecMCP`) let a session on one device
  (e.g. Claude Desktop config) be linked to a Google Drive/OneDrive authorization completed via `pair.html` on
  another device (browser), coordinated through Redis-backed session state. `PairedDeviceInfo.Provider` records
  which backend that session uses (empty/missing on pre-Microsoft-support sessions, treated as Google).
- **MCP server**: mounted at `/mcp` via `ModelContextProtocol.AspNetCore` in `BiatecMCP`, stateless HTTP
  transport, tools discovered from the assembly (`BiatecMCPGoogle`).
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
- **Wallet API (`sign`/`manage-limits`/`rekey` scopes)**: `WalletController` (`BiatecOIDC`) exposes
  `POST /wallet/sign` (signs an Algorand transaction group via the shared `IDriveService`), `GET`/`PUT /wallet/limits`
  (the caller's own daily/weekly/monthly spending limits and their currency), `GET /wallet/limits/currencies`
  (every currency a limit can be set in, with its current USD rate), and `GET`/`POST /wallet/seeds` +
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
  `IExchangeRateService`/`CnbExchangeRateService` (Czech National Bank daily fixing, cached in Redis), and
  checked against `ISpendingLimitService`'s rolling daily (24h)/weekly (7d)/monthly (30d) windows **before** any
  transaction in the group is signed — a group that would exceed a limit never partially signs. An asset that
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
- **Provider access token caching**: no wallet endpoint accepts the caller's Google/Microsoft access token as a
  parameter, ever — it's always resolved by `WalletController.ResolveProviderAccessToken` from a `provider_token`
  claim cached on the bearer token itself, so the exact same Biatec token works from any device/backend, not just
  the one the user originally signed in on. `ProviderAccessTokenProtector`/`IProviderAccessTokenProtector`
  (`BiatecOIDC/BusinessLogic`) AES-256-GCM encrypts it under a **dedicated** key
  (`ProviderTokenProtection:Key`/`IV` — deliberately never `AesOptions`, so the two secrets rotate independently),
  captured in `JwtIssuerController.FinalizeAuthorizeAsync` while the ambient cookie session still has it and
  carried through `JwtIssuerService`'s issued access tokens and Redis-backed authorization-code/refresh-token
  records so it survives the code exchange and every subsequent refresh. A `refresh_token` grant has no ambient
  cookie session of its own, so instead of just carrying the cached access token forward unchanged until it
  expires, the caller's provider **refresh** token is cached the same way (`provider_refresh_token` claim,
  `ProviderAccessTokenProtector.RefreshClaimType`) and spent by `JwtIssuerService.RenewProviderTokenAsync` on
  every Biatec token refresh to mint a fresh provider access token onto the newly-issued Biatec access token —
  `WalletController` also spends it opportunistically, mid-lifetime of a still-valid Biatec token, if a wallet
  call hits a stale cached access token (`UnauthorizedAccessException`), retrying once. Only when there's no
  cached provider refresh token at all, or the provider rejects it (revoked/expired), does the caller fall back
  to needing a fresh interactive `/authorize` sign-in — there is no parameter to work around this with. This is a
  deliberate, security-sensitive trade-off — the whole point is that a relying party only ever needs to hold a
  Biatec token, never the user's own
  Google/Microsoft token, which does widen blast radius if this service is ever compromised; see
  `OIDC_INTEGRATION_GUIDE.md`'s "Provider access token caching" section for the full threat-model writeup and why
  a dedicated key (rather than reusing `AesOptions`) and client-embedded caching (rather than a server-side
  lookup table) were chosen specifically to bound that risk. Because there's no caller-supplied fallback,
  `ProviderAccessTokenProtector`'s constructor fails fast (throws `InvalidOperationException`, same precedent as
  `JwtIssuerService.LoadOrCreateSigningKey`) outside `Development` if the dedicated key is missing/invalid — a
  misconfigured key means the wallet API can't function at all, so that's surfaced immediately rather than as a
  wall of unexplained 401s.
- **Service tiers**: `PortfolioValuationService` (`BiatecMCP`) computes a user's Algorand portfolio value to
  auto-assign Free/Professional/Enterprise tiers (device limits, support SLA) — no billing, purely value-based.

## Conventions and constraints

- Interfaces are prefixed `I` and registered as `Scoped` in each project's `Program.cs`. `GoogleCloudStorageProvider`,
  `MicrosoftCloudStorageProvider`, and `CrossAccountProtectionService` are typed `HttpClient`s (registered via
  `AddHttpClient<T>()`) exposed to callers only through a `Scoped` interface registration
  (`AddScoped<ICloudStorageProvider>(sp => sp.GetRequiredService<T>())`). Follow this pattern for new services —
  do not root-resolve a `Scoped` service from `app.Services` (unsafe under `ValidateScopes`); framework
  `Singleton`s like `IActionDescriptorCollectionProvider` are the one thing safe to touch that way (see the
  startup warm-up below).
- **Startup warm-up (do not remove on a `Program.cs` rewrite)**: both apps force ASP.NET Core to eagerly discover
  and compile the full controller/action/endpoint model right before `app.Run()` —
  `IActionDescriptorCollectionProvider.ActionDescriptors` plus enumerating
  `((IEndpointRouteBuilder)app).DataSources` and touching each `.Endpoints`. This exists so a pod that just
  passed its Kubernetes readiness probe doesn't make whichever real user request lands first pay for assembly
  scanning and route-table construction that has nothing to do with that request.
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
  `mcr.microsoft.com/dotnet/sdk:<ver>`), the version mentioned in this file and `CLAUDE.md`, and
  `BiatecMCP/README.md`. A `TargetFramework` bump alone will build fine locally but ship a Docker image on the
  old runtime.
- This is proprietary software (Scholtz & Company, j.s.a.) — do not add third-party license headers or
  open-source boilerplate.
- Three markdown docs carry deep protocol context and should be kept accurate when touching these areas:
  `BiatecOIDC/OIDC_INTEGRATION_GUIDE.md`, `BiatecOIDC/BIATEC_OIDC_LOGOUT_REQUIREMENTS.md`, and
  `BiatecOIDC/ENTRA_SETUP_GUIDE.md`.
