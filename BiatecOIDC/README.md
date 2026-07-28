# Biatec OIDC / JWT Issuer

An OpenID Connect identity provider that lets whitelisted third-party applications authenticate
users via Biatec's Google sign-in and receive signed JWTs carrying Algorand identity claims
(`algorand_address`).

This service was split out of the original combined `AlgorandGoogleDriveAccount` project so the
OIDC/JWT issuer can be deployed, scaled, and rolled out independently from the `BiatecMCP` MCP
server. It's deployed separately but reachable at the same public host,
`https://google.biatec.io`, via its own Kubernetes Ingress that claims only the OIDC-specific
paths (`/authorize`, `/token`, `/.well-known/*`, `/userinfo`, `/introspect`, `/verify`,
`/connect/endsession`, `/logout`) — everything else on that host is routed to `BiatecMCP`.

It depends on the sibling `BiatecSelfCustodyCore` class library (also referenced by `BiatecMCP`)
to read the user's self-custody Algorand account address for the `algorand_address` claim.

The implementation follows OpenID Connect discovery and token endpoints while preserving
compatibility with a legacy `returnUrl` direct `id_token` flow.

## Endpoints

- `GET /.well-known/openid-configuration` - OIDC discovery metadata.
- `GET /.well-known/jwks.json` - public signing keys for JWT validation.
- `GET /authorize` - authorization endpoint.
  - standard mode: `response_type=code` and exchange at `/token` (PKCE supported/required for
    public clients).
  - legacy mode: `returnUrl` alias with direct `id_token` form POST.
- `POST /token` - exchanges authorization code (with optional PKCE `code_verifier`) and also
  supports refresh token renewal.
- `GET /userinfo` - returns claims from access token.
- `POST /introspect` and `POST /verify` - token activity and verification helpers.
- `GET /connect/endsession` (alias `GET /logout`) - RP-Initiated Logout 1.0.

## Claims in issued tokens

- `email`
- `algorand_address` (optional - omitted if the user never granted Google Drive access)
- `preferred_username` and `name` set to the short identity derived from Algorand address (first 4 + last 4 chars)
- standard claims such as `sub`, `iss`, `aud`, `exp`, `iat`, `nbf`, `jti`

## Whitelisting and client registration

Clients and allowed redirect URLs are configured in `JwtIssuer:Clients` in `appsettings.json`.
Redirect URI matching is an allowlist (with `*` wildcard subdomain support via
`Helper/RedirectUriMatcher.cs`) checked before any authorization response is returned.

## SigningPrivateKeyPem setup (secure and working)

The issuer currently supports **RSA signing** (`RS256`).

- Supported key headers:
  - `-----BEGIN PRIVATE KEY-----` (PKCS#8)
  - `-----BEGIN RSA PRIVATE KEY-----` (PKCS#1)
- Not supported:
  - `-----BEGIN OPENSSH PRIVATE KEY-----`

If you used `ssh-keygen -t rsa -b 4096`, that typically produces OpenSSH private key format, which cannot be imported by the .NET JWT signer in this service.

Generate a compatible RSA key with OpenSSL:

```bash
# Generate RSA 4096 PKCS#8 private key
openssl genpkey -algorithm RSA -pkeyopt rsa_keygen_bits:4096 -out jwt-signing-private.pem

# Optional: extract public key for verification/debugging
openssl rsa -pubout -in jwt-signing-private.pem -out jwt-signing-public.pem
```

Set the key in configuration in one of these ways:

1. Inline PEM content in `JwtIssuer:SigningPrivateKeyPem` using escaped newlines (`\\n`)
2. File path in `JwtIssuer:SigningPrivateKeyPem` (the service resolves existing file paths and reads the PEM file)

Example inline value:

```json
"JwtIssuer": {
  "KeyId": "biatec-main-key-2026-05",
  "SigningPrivateKeyPem": "-----BEGIN PRIVATE KEY-----\\nMIIEv...\\n-----END PRIVATE KEY-----"
}
```

## About Ed25519 / EdDSA

`Ed25519` JWT signing (`EdDSA`) is currently **not enabled** in this implementation because the active `System.IdentityModel.Tokens.Jwt` / `Microsoft.IdentityModel.Tokens` stack in this project does not expose EdDSA signing primitives in the current package set.

Supported algorithms in this service today:

- `RS256` (default)
- The library also supports ECDSA families (`ES256`, `ES384`, `ES512`), but this service is currently wired for `RS256` only.

## Detailed integration guide

See `OIDC_INTEGRATION_GUIDE.md` for full configuration, security guidance, and a Copilot-ready
prompt for implementing this flow in destination projects, and
`BIATEC_OIDC_LOGOUT_REQUIREMENTS.md` for RP-Initiated Logout requirements.

## Legal

- **Company**: Scholtz & Company, j.s.a. (Slovakia)
- **License**: proprietary software owned by Scholtz & Company, j.s.a.
