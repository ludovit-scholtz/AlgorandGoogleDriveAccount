# CLAUDE.md

This file guides Claude Code when working in this repository. It must stay in sync with
[.github/copilot-instructions.md](.github/copilot-instructions.md) — the two files serve the same purpose for
different AI assistants (Claude Code vs. GitHub Copilot). Whenever you update one, update the other to match.

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

Both are served from the same public host, `https://google.biatec.io`, via path-based ingress routing (see
"Kubernetes / ingress routing" below) — the MCP endpoint stays at `https://google.biatec.io/mcp/`. They share one
piece of self-custody infrastructure, `BiatecSelfCustodyCore` (see below), because the OIDC provider embeds an
`algorand_address` claim in issued tokens, which requires reading the same Drive/OneDrive-backed account BiatecMCP
manages.

## Solution layout

- `BiatecSelfCustodyCore/` — shared class library (net10.0, `Microsoft.NET.Sdk`), referenced by both `BiatecMCP` and
  `BiatecOIDC`. Holds the security-sensitive self-custody code so it exists in exactly one place:
  - `Repository/GoogleDriveFileStore.cs`, `OneDriveFileStore.cs` — dumb byte-level read/write against Google Drive
    (folder search/create) and OneDrive's app-folder special folder (Graph REST, no SDK) respectively
  - `Repository/ICloudAccountRepository.cs` + `CloudAccountRepository.cs` — the thing services actually inject;
    owns the AES encrypt/decrypt + ARC76 account-derivation logic **once**, dispatches to whichever file store
    matches the caller's `StorageProvider`, resolving the access token either explicitly (device-pairing path) or
    ambiently (`IGoogleAuthProvider`/`IMicrosoftAuthProvider` for the cookie-session path)
  - `BusinessLogic/IDriveService.cs`, `DriveService.cs` — sign transactions, get account address (both take a
    `StorageProvider` parameter)
  - `BusinessLogic/IMicrosoftAuthProvider.cs`/`MicrosoftAuthProvider.cs` — Microsoft analogue of
    `Google.Apis.Auth.AspNetCore3`'s `IGoogleAuthProvider`, reads the current user's Microsoft token via
    `HttpContext.GetTokenAsync("Microsoft", "access_token")`
  - `BusinessLogic/StorageAccessVerifier.cs` — checks whether a token actually grants storage-write access
    (`drive.file` / `Files.ReadWrite.AppFolder`) before a session/token is finalized
  - `BusinessLogic/OpenIdConnectIncrementalAuth.cs` — shared `OnRedirectToIdentityProvider` logic (both apps, both
    schemes) for incremental-scope + forced-consent re-challenges
  - `Helper/AesEncryptionHelper.cs` — email-bound AES-256 encryption of the stored account
  - `Model/Configuration.cs`, `AesOptions.cs`, `MicrosoftEntraConfiguration.cs`, `StorageProvider.cs`
    (+ `StorageProviderExtensions.Parse`, defaults to Google), `AuthSchemeNames.cs` (scheme names + the
    `biatec_idp` claim type)
- `BiatecMCP/` — the MCP server + self-custody web/API project (net10.0, `Microsoft.NET.Sdk.Web`)
  - `Controllers/` — `DevicePairingController` (now provider-aware: `pair-device?idp=`, `RequestStorageAccess`,
    `StorageAccessCallback`), `DriveController`
  - `BusinessLogic/` — `DevicePairingService`, `GoogleAuthorizationService`, `CrossAccountProtectionService`,
    `PortfolioValuationService` (+ their `I*Service` interfaces)
  - `Model/` — `DevicePairingModels` (`PairedDeviceInfo.Provider`), `McpTransferLimitsConfiguration`,
    `AlgodConfiguration`, `CrossAccountProtectionConfiguration`, plus local `RedisConfiguration`/`CorsConfiguration`
    copies
  - `MCP/BiatecMCPGoogle.cs` — MCP tool definitions exposed to AI clients (e.g. `getAlgorandAddress`)
  - `Helper/` — `SecureTokenGenerator`, `TransferPolicy`
  - `wwwroot/` — static pages: `index.html`, `pair.html` (device pairing UI, Google/Microsoft picker),
    `privacy.html`, `terms.html`
- `BiatecOIDC/` — the OIDC/JWT issuer web/API project (net10.0, `Microsoft.NET.Sdk.Web`)
  - `Controllers/JwtIssuerController.cs` — `/authorize` (+ `idp` fast track), `/select-provider` (picker page),
    `/authorize/challenge`, `/authorize/callback` (verifies storage-write access before finalizing)
  - `BusinessLogic/JwtIssuerService.cs` (+ `IJwtIssuerService`) — depends on `BiatecSelfCustodyCore`'s
    `IDriveService` for the `algorand_address` claim
  - `Helper/RedirectUriMatcher.cs` — OIDC redirect URI matching incl. wildcard support
  - `Model/JwtIssuerModels.cs`, plus local `RedisConfiguration`/`CorsConfiguration` copies
  - `OIDC_INTEGRATION_GUIDE.md`, `BIATEC_OIDC_LOGOUT_REQUIREMENTS.md`, `ENTRA_SETUP_GUIDE.md`
- `BiatecMCPTests/` — NUnit + Moq tests for `BiatecMCP` + `BiatecSelfCustodyCore` (device pairing, Drive controller,
  AES encryption, transfer policy, Google authorization scope checks, `OneDriveFileStore`, `StorageAccessVerifier`,
  `StorageProviderExtensions`)
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

Both services require Redis (`Redis:ConnectionString` in their respective `appsettings.json`) and Google OAuth 2.0
credentials (`App:ClientId`/`App:ClientSecret`) to run. CI (`.github/workflows/build-api.yml`) builds/pushes two
Docker images (one per service) and applies both straight to the Kubernetes cluster on push to `master` — no
staging server or SSH involved. See [docs/CICD_GITHUB_ACTIONS.md](docs/CICD_GITHUB_ACTIONS.md) for the required
GitHub secrets and [docs/KUBE_CONFIG_SECURITY.md](docs/KUBE_CONFIG_SECURITY.md) for why the CI kubeconfig is
namespace-scoped and short-lived. There is no automated test job in CI, so run tests locally before pushing.

## Kubernetes / ingress routing

Both services run as separate Deployments/Services in the `biatec` namespace but share the same public host,
`google.biatec.io`, via **two separate Ingress objects**:

- `k8s/main/deployment-mcp.yaml` — `biatec-mcp-app-deployment`/`biatec-mcp-service`/`biatec-mcp-ingress`. Catch-all
  path (`/(.*)`, `rewrite-target: /$1`) — this is the default backend for the host, so `/mcp`, `/api/drive`,
  `/api/device`, `/`, and all static `wwwroot` pages keep resolving here unchanged.
- `k8s/main/deployment-oidc.yaml` — `biatec-oidc-app-deployment`/`biatec-oidc-service`/`biatec-oidc-ingress`.
  Claims only the OIDC-specific literal paths (`/.well-known`, `/authorize`, `/token`, `/userinfo`, `/introspect`,
  `/verify`, `/connect/endsession`, `/logout`, `/select-provider`, `/oidc/signin-google`, `/oidc/signin-microsoft`),
  no rewrite. nginx-ingress matches literal/prefix locations ahead of the other Ingress's regex catch-all
  regardless of object order, so this reliably carves out just those paths. `BiatecOIDC`'s Google **and** Microsoft
  OIDC handlers use non-default `CallbackPath`s (`/oidc/signin-google`, `/oidc/signin-microsoft`) specifically so
  they land here and not on `BiatecMCP`'s catch-all (which can't decrypt this app's correlation cookie — separate
  processes, no shared Data Protection key ring). `BiatecMCP` keeps the framework's default `/signin-google` and
  a `/signin-microsoft` CallbackPath, both fine as-is since its ingress is the catch-all.

Both deployments reuse the same secrets (`google-account-main-app-secret` for app config,
`csharp-cert`/`csharp-cert-password` for the internal Kestrel HTTPS cert) — there was no need to provision new
ones. Config is split per-service: `k8s/main/conf-mcp/` / `biatec-mcp-conf` and `k8s/main/conf-oidc/` /
`biatec-oidc-conf`.

## Architecture notes

- **Self-custody model**: Algorand private keys are encrypted per-email via `AesEncryptionHelper`
  (`BiatecSelfCustodyCore`) and stored as a file (`AVMAccount.dat` by default) in the user's own Google Drive
  folder or OneDrive app folder, depending which provider they signed in with. Biatec servers only decrypt
  in-memory during an explicitly authorized signing operation — never persist plaintext keys.
- **Multi-provider auth**: both `BiatecMCP` and `BiatecOIDC` independently configure two authentication schemes —
  Google via `Google.Apis.Auth.AspNetCore3` (`AddGoogleOpenIdConnect`) and Microsoft Entra ID via the plain
  `AddOpenIdConnect(AuthSchemeNames.Microsoft, ...)` handler pointed at
  `https://login.microsoftonline.com/{TenantId}/v2.0` — both sign into the same cookie scheme, so `[Authorize]`
  endpoints don't care which provider was used. Google scopes: `openid profile email` +
  `DriveService.Scope.DriveFile`. Microsoft scopes: `openid profile email offline_access` +
  `https://graph.microsoft.com/Files.ReadWrite.AppFolder`. Each scheme's `OnTokenValidated` stamps a
  `biatec_idp` claim (`"Google"`/`"Microsoft"`, `AuthSchemeNames.IdpClaimType`) onto the signed-in principal so
  later code knows which storage backend to use. See `BiatecOIDC/ENTRA_SETUP_GUIDE.md` for the Entra app
  registration this depends on. Cross-Account Protection (Google RISC) lives only in `BiatecMCP` and is supported
  but disabled by default (`CrossAccountProtection:Enabled`).
- **Provider picker / fast track**: a user chooses Google or Microsoft via `pair.html`'s two buttons (`BiatecMCP`)
  or `BiatecOIDC`'s `/select-provider` page; either can be skipped with `?idp=google`/`?idp=microsoft` on
  `/api/device/pair-device` or `/authorize`. Before finalizing either flow,
  `BiatecSelfCustodyCore/BusinessLogic/StorageAccessVerifier.cs` confirms the fresh token actually has
  storage-write access (declining just that consent checkbox is possible even while completing sign-in); if
  missing, the browser is sent through one incremental-consent round-trip (forced fresh consent screen,
  `OpenIdConnectIncrementalAuth`) before the pairing/OIDC code is finalized, capped at one retry to avoid loops.
- **Device pairing**: `DevicePairingService`/`DevicePairingController` (`BiatecMCP`) let a session on one device
  (e.g. Claude Desktop config) be linked to a Google Drive/OneDrive authorization completed via `pair.html` on
  another device (browser), coordinated through Redis-backed session state. `PairedDeviceInfo.Provider` records
  which backend that session uses (empty/missing on pre-Microsoft-support sessions, treated as Google).
- **MCP server**: mounted at `/mcp` via `ModelContextProtocol.AspNetCore` in `BiatecMCP`, stateless HTTP transport,
  tools discovered from the assembly (`BiatecMCPGoogle`).
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

- Interfaces are prefixed `I` and registered as `Scoped` in each project's `Program.cs`; `GoogleDriveFileStore` is
  the only `Singleton` (`OneDriveFileStore`/`StorageAccessVerifier` are typed `HttpClient`s, registered via
  `AddHttpClient<T>()`). Follow this pattern for new services.
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
  (endpoints, claims, redirect-URI/logout allowlist rules, signing-key format). Use this instead of reading the two
  full guide docs above when working on `/authorize`, `/token`, `/userinfo`, `/introspect`, `/verify`,
  `/connect/endsession`, `/logout`, `JwtIssuerService.cs`, `JwtIssuerController.cs`, `RedirectUriMatcher.cs`, or
  `JwtIssuer:*` config (all in `BiatecOIDC/`).
