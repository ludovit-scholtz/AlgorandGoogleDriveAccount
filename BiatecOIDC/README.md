# Biatec OIDC / JWT Issuer

An OpenID Connect identity provider that lets whitelisted third-party applications authenticate
users via Biatec's Google or Microsoft Entra ID sign-in and receive signed JWTs carrying Algorand
identity claims (`algorand_address`).

This service was split out of the original combined `AlgorandGoogleDriveAccount` project so the
OIDC/JWT issuer can be deployed, scaled, and rolled out independently from the `BiatecMCP` MCP
server. It's deployed separately, and reachable at two public hosts:

- `https://oidc.biatec.io` — its own dedicated domain, the recommended host for new integrations,
  routed entirely to this service via its own Kubernetes Ingress (`biatec-oidc-domain-ingress`).
- `https://google.biatec.io` — the original shared host, kept working as a legacy alias for
  existing integrations, via a separate Ingress that claims only the OIDC-specific paths
  (`/authorize`, `/token`, `/.well-known/*`, `/userinfo`, `/introspect`, `/verify`,
  `/connect/endsession`, `/logout`, `/select-provider`, `/oidc/signin-google`,
  `/oidc/signin-microsoft`) — everything else on that host is routed to `BiatecMCP`.

Both hosts are internally self-consistent: the `iss` claim and discovery `issuer` field are derived
from whichever host actually received the request (see `JwtIssuerService.GetIssuer`), not hardcoded
to one value, so neither host's discovery document is broken by the other's existence.

It depends on the sibling `BiatecSelfCustodyCore` class library (also referenced by `BiatecMCP`)
to read the user's self-custody Algorand account address (Google Drive or OneDrive, depending on
which provider they signed in with) for the `algorand_address` claim.

## Choosing a provider

When `/authorize` needs the user to sign in, it redirects to `/select-provider`, a picker page
with "Continue with Google" / "Continue with Microsoft" buttons. Pass `?idp=google` or
`?idp=microsoft` on the `/authorize` request itself to skip the picker and go straight to that
provider (the "fast track"). Before issuing the authorization code/token, the callback verifies
the fresh token actually has storage-write access to the chosen backend and, if the user declined
just that consent checkbox, sends them through one forced-consent round-trip first. See
`ENTRA_SETUP_GUIDE.md` for setting up the Microsoft Entra ID side of this.

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

## Scopes

Requested via `scope` on `/authorize` (space-separated). Supported scopes:

| Scope | What it does | Requires `AllowedScopes` entry? |
| --- | --- | --- |
| `openid` | Standard OIDC identity assertion. **Required** on every request - omitting it fails with `invalid_scope`. | Always granted |
| `profile` | Adds `preferred_username`/`name` (the short Algorand identity). | Always granted |
| `email` | Adds the `email` claim. | Always granted |
| `sign` | Grants `POST /wallet/sign` (sign an Algorand transaction group). Stamps a `sign: "true"` access-token claim. | **Yes** |
| `manage-limits` | Grants `PUT /wallet/limits` (set the caller's daily/weekly/monthly spending limits). Stamps a `manage-limits: "true"` access-token claim. `GET /wallet/limits` (reading them) only needs `openid`, not this scope. | **Yes** |
| `rekey` | The strictest scope. Grants `POST /wallet/sign` permission to sign a transaction group that contains an Algorand `rekey` field (permanently reassigns which key controls the account) - without it, such a group is refused with 403 even if `sign` is present. Also grants `POST /wallet/seeds` (mint a spare seed ahead of an on-chain rekey). Stamps a `rekey: "true"` access-token claim. **The consent screen shows a distinct danger warning when a client requests this scope** - a leaked token/session carrying it risks total, irreversible loss of every asset in the account. | **Yes** |

`sign`, `manage-limits`, and `rekey` are **not** included in a client's `AllowedScopes` by default - see
"Whitelisting and client registration" below for how to add them. Requesting one without it being
allowlisted fails the whole `/authorize` request with `invalid_scope` (the error description names
exactly which scope(s) aren't allowlisted) - it's rejected loudly rather than silently granted
without it, so you always get a clear signal instead of a token that mysteriously doesn't do what
you expected.

A scope this service has never heard of at all (a typo, or one an OIDC client library
auto-appends on its own - e.g. some MSAL/Azure AD-flavored clients send a literal `.default` scope
when none is explicitly configured) is silently dropped instead, since there's nothing to fix on
either side and failing the whole login over library noise would be worse. The scope you actually
end up with is always visible in the token response's `scope` field.

## Whitelisting and client registration

Clients and allowed redirect URLs are configured in `JwtIssuer:Clients` in `appsettings.json`.
Redirect URI matching is an allowlist (with `*` wildcard subdomain support via
`Helper/RedirectUriMatcher.cs`) checked before any authorization response is returned.

Each client also has an `AllowedScopes` list - this is what actually gates `sign`/`manage-limits`/`rekey`
(see "Scopes" above). To let a client use the wallet API, add the ones it needs:

```json
"AllowedScopes": ["openid", "profile", "email", "sign", "manage-limits", "rekey"]
```

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
