# GitHub Copilot instructions

This file must stay in sync with [CLAUDE.md](../CLAUDE.md) at the repo root — the two files carry the same
project knowledge for different AI assistants (GitHub Copilot vs. Claude Code). Whenever you update one, update
the other to match.

## Project overview

Biatec — two independently deployed ASP.NET Core 10 services that used to be one app and were split apart:

- **BiatecMCP** gives AI assistants (via the Model Context Protocol) self-custody access to Algorand accounts.
  Private keys are AES-256 encrypted, bound to the user's email address, and stored only in the user's own Google
  Drive — never on Biatec's servers.
- **BiatecOIDC** is an OpenID Connect identity provider (JWT issuer) so whitelisted third-party apps can
  authenticate users via Google and receive Algorand-identity claims.

Both are served from the same public host, `https://google.biatec.io`, via path-based ingress routing (see
"Kubernetes / ingress routing" below) — the MCP endpoint stays at `https://google.biatec.io/mcp/`. They share one
piece of self-custody infrastructure, `BiatecSelfCustodyCore` (see below), because the OIDC provider embeds an
`algorand_address` claim in issued tokens, which requires reading the same Google-Drive-backed account BiatecMCP
manages.

## Solution layout

- `BiatecSelfCustodyCore/` — shared class library (net10.0, `Microsoft.NET.Sdk`), referenced by both `BiatecMCP`
  and `BiatecOIDC`. Holds the security-sensitive Google Drive self-custody code so it exists in exactly one place:
  - `Repository/GoogleDriveRepository.cs` — Google Drive API access, loads/creates the encrypted account file
  - `BusinessLogic/IDriveService.cs`, `DriveService.cs` — sign transactions, get account address
  - `Helper/AesEncryptionHelper.cs` — email-bound AES-256 encryption of the stored account
  - `Model/Configuration.cs`, `AesOptions.cs` — bound from the `App`/`AesOptions` config sections
- `BiatecMCP/` — the MCP server + self-custody web/API project (net10.0, `Microsoft.NET.Sdk.Web`)
  - `Controllers/` — `DevicePairingController`, `DriveController`
  - `BusinessLogic/` — `DevicePairingService`, `GoogleAuthorizationService`, `CrossAccountProtectionService`,
    `PortfolioValuationService` (+ their `I*Service` interfaces)
  - `Model/` — `DevicePairingModels`, `McpTransferLimitsConfiguration`, `AlgodConfiguration`,
    `CrossAccountProtectionConfiguration`, plus local `RedisConfiguration`/`CorsConfiguration` copies
  - `MCP/BiatecMCPGoogle.cs` — MCP tool definitions exposed to AI clients (e.g. `getAlgorandAddress`)
  - `Helper/` — `SecureTokenGenerator`, `TransferPolicy`
  - `wwwroot/` — static pages: `index.html`, `pair.html` (device pairing UI), `privacy.html`, `terms.html`
- `BiatecOIDC/` — the OIDC/JWT issuer web/API project (net10.0, `Microsoft.NET.Sdk.Web`)
  - `Controllers/JwtIssuerController.cs`
  - `BusinessLogic/JwtIssuerService.cs` (+ `IJwtIssuerService`) — depends on `BiatecSelfCustodyCore`'s
    `IDriveService` for the `algorand_address` claim
  - `Helper/RedirectUriMatcher.cs` — OIDC redirect URI matching incl. wildcard support
  - `Model/JwtIssuerModels.cs`, plus local `RedisConfiguration`/`CorsConfiguration` copies
  - `OIDC_INTEGRATION_GUIDE.md`, `BIATEC_OIDC_LOGOUT_REQUIREMENTS.md`
- `BiatecMCPTests/` — NUnit + Moq tests for `BiatecMCP` + `BiatecSelfCustodyCore` (device pairing, Drive
  controller, AES encryption, transfer policy, Google authorization scope checks)
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
staging server or SSH involved. See [docs/CICD_GITHUB_ACTIONS.md](../docs/CICD_GITHUB_ACTIONS.md) for the required
GitHub secrets and [docs/KUBE_CONFIG_SECURITY.md](../docs/KUBE_CONFIG_SECURITY.md) for why the CI kubeconfig is
namespace-scoped and short-lived. There is no automated test job in CI, so run tests locally before pushing.

## Kubernetes / ingress routing

Both services run as separate Deployments/Services in the `biatec` namespace but share the same public host,
`google.biatec.io`, via **two separate Ingress objects**:

- `k8s/main/deployment-mcp.yaml` — `biatec-mcp-app-deployment`/`biatec-mcp-service`/`biatec-mcp-ingress`.
  Catch-all path (`/(.*)`, `rewrite-target: /$1`) — this is the default backend for the host, so `/mcp`,
  `/api/drive`, `/api/device`, `/`, and all static `wwwroot` pages keep resolving here unchanged.
- `k8s/main/deployment-oidc.yaml` — `biatec-oidc-app-deployment`/`biatec-oidc-service`/`biatec-oidc-ingress`.
  Claims only the OIDC-specific literal paths (`/.well-known`, `/authorize`, `/token`, `/userinfo`,
  `/introspect`, `/verify`, `/connect/endsession`, `/logout`), no rewrite. nginx-ingress matches literal/prefix
  locations ahead of the other Ingress's regex catch-all regardless of object order, so this reliably carves out
  just those paths.

Both deployments reuse the same secrets (`google-account-main-app-secret` for app config,
`csharp-cert`/`csharp-cert-password` for the internal Kestrel HTTPS cert) — there was no need to provision new
ones. Config is split per-service: `k8s/main/conf-mcp/` / `biatec-mcp-conf` and `k8s/main/conf-oidc/` /
`biatec-oidc-conf`.

## Architecture notes

- **Self-custody model**: Algorand private keys are encrypted per-email via `AesEncryptionHelper`
  (`BiatecSelfCustodyCore`) and stored as a file (`AVMAccount.dat` by default) in the user's own Google Drive
  folder. Biatec servers only decrypt in-memory during an explicitly authorized signing operation — never persist
  plaintext keys.
- **Auth**: both `BiatecMCP` and `BiatecOIDC` independently configure Google OpenID Connect via
  `Google.Apis.Auth.AspNetCore3`, cookie-based session, scopes limited to `openid profile email` plus
  `DriveService.Scope.DriveFile`. They use the same Google Cloud OAuth client (`App:ClientId`/`ClientSecret`).
  Cross-Account Protection (Google RISC) lives only in `BiatecMCP` and is supported but disabled by default
  (`CrossAccountProtection:Enabled`).
- **Device pairing**: `DevicePairingService`/`DevicePairingController` (`BiatecMCP`) let a session on one device
  (e.g. Claude Desktop config) be linked to a Google Drive authorization completed via `pair.html` on another
  device (browser), coordinated through Redis-backed session state.
- **MCP server**: mounted at `/mcp` via `ModelContextProtocol.AspNetCore` in `BiatecMCP`, stateless HTTP
  transport, tools discovered from the assembly (`BiatecMCPGoogle`).
- **JWT issuer / OIDC provider**: `JwtIssuerService` + `JwtIssuerController` (`BiatecOIDC`) implement OIDC
  discovery, authorize, token, userinfo, introspect/verify, and RP-initiated logout endpoints. RS256 only today;
  client whitelisting and redirect URI allowlists (with wildcard support) live under `JwtIssuer:Clients`. See
  `BiatecOIDC/OIDC_INTEGRATION_GUIDE.md` and `BiatecOIDC/BIATEC_OIDC_LOGOUT_REQUIREMENTS.md` for the full
  contract.
- **Service tiers**: `PortfolioValuationService` (`BiatecMCP`) computes a user's Algorand portfolio value to
  auto-assign Free/Professional/Enterprise tiers (device limits, support SLA) — no billing, purely value-based.

## Conventions and constraints

- Interfaces are prefixed `I` and registered as `Scoped` in each project's `Program.cs`; `GoogleDriveRepository`
  is the only `Singleton`. Follow this pattern for new services.
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
