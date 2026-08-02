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

## Available MCP tools

- **`getAlgorandAddress`** — returns the signed-in account's Algorand address (read from the bearer token's own
  `algorand_address` claim, falling back to the primary seed from `GET /wallet/seeds`).
- **`transferAsset`** — signs and broadcasts a native ALGO payment or ASA transfer. An empty `receiverAccount`
  performs a self-transfer. Requires the `sign` scope; BiatecOIDC enforces the caller's configured spending limit.
- **`optIn`** — opts the account in to an ASA (a zero-amount self-transfer, the standard Algorand pattern).
  Requires the `sign` scope.

## Connecting an MCP client

Point your MCP client at:

```
https://mcp.biatec.io/mcp
```

(stage: `https://stage.mcp.biatec.io/mcp`). Any client that implements the MCP Authorization spec's OAuth
discovery flow (Claude Desktop, Claude.ai, recent VS Code MCP support, etc.) will handle steps 1–5 above
automatically the first time it connects — there is no manual "pairing" step to complete separately, and no
session ID to configure. If your client supports a raw MCP server URL field, that is all you need to enter.

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
