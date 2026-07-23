# CLAUDE.md

This file guides Claude Code when working in this repository. It must stay in sync with
[.github/copilot-instructions.md](.github/copilot-instructions.md) — the two files serve the same purpose for
different AI assistants (Claude Code vs. GitHub Copilot). Whenever you update one, update the other to match.

## Project overview

Biatec MCP Server — an ASP.NET Core 8 (`AlgorandGoogleDriveAccount`) service that gives AI assistants (via the Model
Context Protocol) self-custody access to Algorand accounts. Private keys are AES-256 encrypted, bound to the
user's email address, and stored only in the user's own Google Drive — never on Biatec's servers. The service also
acts as an OpenID Connect identity provider (JWT issuer) so whitelisted third-party apps can authenticate users via
Google and receive Algorand-identity claims.

## Solution layout

- `AlgorandGoogleDriveAccount/` — the web/API/MCP project (net8.0, `Microsoft.NET.Sdk.Web`)
  - `Controllers/` — `DevicePairingController`, `DriveController`, `JwtIssuerController`
  - `BusinessLogic/` — services and their interfaces (`I*Service` + implementation), e.g. `DriveService`,
    `DevicePairingService`, `GoogleAuthorizationService`, `CrossAccountProtectionService`,
    `PortfolioValuationService`, `JwtIssuerService`
  - `Repository/` — `GoogleDriveRepository` (Google Drive API access)
  - `Model/` — POCOs bound from `appsettings.json` sections (`Configuration`, `AesOptions`, `RedisConfiguration`,
    `CorsConfiguration`, `CrossAccountProtectionConfiguration`, `AlgodConfiguration`, `JwtIssuerConfiguration`,
    `DevicePairingModels`, `JwtIssuerModels`)
  - `MCP/BiatecMCPGoogle.cs` — MCP tool definitions exposed to AI clients (e.g. `getAlgorandAddress`)
  - `Helper/` — `AesEncryptionHelper` (email-bound AES-256 encryption), `RedirectUriMatcher` (OIDC redirect URI
    matching incl. wildcard support)
  - `wwwroot/` — static pages: `index.html`, `pair.html` (device pairing UI), `privacy.html`, `terms.html`
- `AlgoranGoogleDriveAccountTests/` — NUnit + Moq test project (note the project name drops one "d" from "Algorand" —
  this is intentional/historical, don't "fix" the typo)

## Build, test, run

```bash
dotnet build AlgorandGoogleDriveAccount.sln
dotnet test AlgoranGoogleDriveAccountTests/AlgoranGoogleDriveAccountTests.csproj
dotnet run --project AlgorandGoogleDriveAccount/AlgorandGoogleDriveAccount.csproj
```

Requires Redis (`Redis:ConnectionString` in `appsettings.json`) and Google OAuth 2.0 credentials
(`App:ClientId`/`App:ClientSecret`) to run. CI (`.github/workflows/build-api.yml`) builds/pushes a Docker image and
applies it straight to the Kubernetes cluster on push to `master` — no staging server or SSH involved anymore. See
[docs/CICD_GITHUB_ACTIONS.md](docs/CICD_GITHUB_ACTIONS.md) for the required GitHub secrets and
[docs/KUBE_CONFIG_SECURITY.md](docs/KUBE_CONFIG_SECURITY.md) for why the CI kubeconfig is namespace-scoped and
short-lived. There is no automated test job in CI, so run tests locally before pushing.

## Architecture notes

- **Self-custody model**: Algorand private keys are encrypted client-conceptually per-email via
  `AesEncryptionHelper` and stored as a file (`AVMAccount.dat` by default) in the user's own Google Drive folder.
  Biatec servers only decrypt in-memory during an explicitly authorized signing operation — never persist plaintext
  keys.
- **Auth**: Google OpenID Connect via `Google.Apis.Auth.AspNetCore3`, cookie-based session, scopes limited to
  `openid profile email` plus `DriveService.Scope.DriveFile`. Cross-Account Protection (Google RISC) is supported
  but disabled by default (`CrossAccountProtection:Enabled`).
- **Device pairing**: `DevicePairingService`/`DevicePairingController` let a session on one device (e.g. Claude
  Desktop config) be linked to a Google Drive authorization completed via `pair.html` on another device
  (browser), coordinated through Redis-backed session state.
- **MCP server**: mounted at `/mcp` via `ModelContextProtocol.AspNetCore`, stateless HTTP transport, tools
  discovered from the assembly (`BiatecMCPGoogle`).
- **JWT issuer / OIDC provider**: `JwtIssuerService` + `JwtIssuerController` implement OIDC discovery
  (`/.well-known/openid-configuration`, `/.well-known/jwks.json`), `/authorize`, `/token`, `/userinfo`,
  `/introspect`, `/verify`. Supports both standard `response_type=code` and a legacy `returnUrl` direct
  `id_token` flow. RS256 only today (PKCS#8/PKCS#1 PEM keys); EdDSA is not supported by the current
  `Microsoft.IdentityModel.Tokens` version in use. Client whitelisting and redirect URI allowlists live under
  `JwtIssuer:Clients` in `appsettings.json`; see `RedirectUriMatcher` for wildcard redirect URI matching rules and
  `OIDC_INTEGRATION_GUIDE.md` for the full integration contract.
- **Service tiers**: `PortfolioValuationService` computes a user's Algorand portfolio value to auto-assign
  Free/Professional/Enterprise tiers (device limits, support SLA) — no billing, purely value-based.

## Conventions and constraints

- Interfaces are prefixed `I` and registered as `Scoped` in `Program.cs`; `GoogleDriveRepository` is the only
  `Singleton`. Follow this pattern for new services.
- Configuration is strongly typed via `IOptions<T>` bound from named sections in `appsettings.json` — add new
  settings as a new `Model/*.cs` POCO + `builder.Services.Configure<T>(...)` rather than reading `IConfiguration`
  directly in business logic.
- Never log or persist decrypted private key material. Treat `AesEncryptionHelper` and anything touching
  `StorageFileName`/private keys as security-sensitive; changes there warrant extra scrutiny.
- Redirect URI validation for OIDC (`/authorize`) must remain an allowlist check against `JwtIssuer:Clients` —
  do not loosen this to permissive matching without explicit instruction.
- This is proprietary software (Scholtz & Company, j.s.a.) — do not add third-party license headers or open-source
  boilerplate.
- Two markdown docs carry deep protocol context and should be kept accurate when touching these areas:
  `AlgorandGoogleDriveAccount/OIDC_INTEGRATION_GUIDE.md` and
  `AlgorandGoogleDriveAccount/BIATEC_OIDC_LOGOUT_REQUIREMENTS.md`.

## Skills

- `biatec-oidc-jwt` (`.claude/skills/biatec-oidc-jwt/SKILL.md`) — condensed reference for the OIDC/JWT issuer
  (endpoints, claims, redirect-URI/logout allowlist rules, signing-key format). Use this instead of reading the two
  full guide docs above when working on `/authorize`, `/token`, `/userinfo`, `/introspect`, `/verify`,
  `/connect/endsession`, `/logout`, `JwtIssuerService.cs`, `JwtIssuerController.cs`, `RedirectUriMatcher.cs`, or
  `JwtIssuer:*` config.
