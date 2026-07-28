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
  - `Model/Configuration.cs`, `AesOptions.cs`, `MicrosoftEntraConfiguration.cs`, `AuthSchemeNames.cs` (just the
    `biatec_idp` claim type constant — each provider owns its own name via `ICloudStorageProvider.Name`)
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
  - `BusinessLogic/JwtIssuerService.cs` (+ `IJwtIssuerService`) — depends on `BiatecSelfCustodyCore`'s
    `IDriveService` for the `algorand_address` claim
  - `Helper/RedirectUriMatcher.cs` — OIDC redirect URI matching incl. wildcard support
  - `Model/JwtIssuerModels.cs`, plus local `RedisConfiguration`/`CorsConfiguration` copies
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
credentials (`App:ClientId`/`App:ClientSecret`), and Microsoft Entra ID credentials
(`MicrosoftEntra:TenantId`/`ClientId`/`ClientSecret` — see `BiatecOIDC/ENTRA_SETUP_GUIDE.md`) to run. CI
(`.github/workflows/build-api.yml`) builds/pushes two Docker images (one per service) and applies both straight to
the Kubernetes cluster on push to `master` — no staging server or SSH involved. See
[docs/CICD_GITHUB_ACTIONS.md](../docs/CICD_GITHUB_ACTIONS.md) for the required GitHub secrets and
[docs/KUBE_CONFIG_SECURITY.md](../docs/KUBE_CONFIG_SECURITY.md) for why the CI kubeconfig is namespace-scoped and
short-lived. There is no automated test job in CI, so run tests locally before pushing.

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

## Architecture notes

- **Self-custody model**: Algorand private keys are encrypted per-email via `AesEncryptionHelper`
  (`BiatecSelfCustodyCore`) and stored as a file (`AVMAccount.dat` by default) in the user's own Google Drive
  folder or OneDrive app folder, depending which provider they signed in with. Biatec servers only decrypt
  in-memory during an explicitly authorized signing operation — never persist plaintext keys.
- **Pluggable cloud storage providers**: `ICloudStorageProvider` (`BiatecSelfCustodyCore/Providers/`) is the
  single extension point for a new storage backend — implement it, register it in DI, done; `ICloudAccountRepository`,
  `ICloudStorageProviderCatalog`, and both picker UIs (`BiatecMCP`'s `GET /api/device/providers`, `BiatecOIDC`'s
  `/select-provider`) all resolve providers dynamically and need zero code changes for provider #3+. To add one:
  implement `ICloudStorageProvider`, register it in **both** `BiatecMCP/Program.cs` and `BiatecOIDC/Program.cs`
  the same way as the existing Google/Microsoft blocks (`AddHttpClient<T>()` +
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
  `OIDC_INTEGRATION_GUIDE.md` for the full integration contract.
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
