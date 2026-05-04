# Biatec OIDC Logout Requirements (Standards-Based)

This document defines what Biatec OIDC exposes so the Capitalism game and master portals can perform a complete sign-out (application session + identity-provider session) using OpenID Connect standards.

## Goal

When a player clicks Logout:
1. Local app session is cleared.
2. Browser is redirected to Biatec logout endpoint.
3. Biatec invalidates the IdP session.
4. Browser returns to the app logout callback page.

## Standards Target

Use OpenID Connect RP-Initiated Logout 1.0:
- Spec: OpenID Connect RP-Initiated Logout 1.0
- Primary endpoint: `end_session_endpoint`

## Provider Capabilities

### 1) Discovery metadata
The OIDC discovery document includes:
- `end_session_endpoint`

Also published for compatibility signaling:
- `frontchannel_logout_supported: false`
- `backchannel_logout_supported: false`

### 2) RP-initiated logout endpoint behavior
Logout endpoint:
- `GET /connect/endsession`
- Alias: `GET /logout`

Supported parameters:
- `id_token_hint` (recommended)
- `post_logout_redirect_uri` (recommended)
- `state` (recommended)
- `client_id` (recommended for compatibility)

Behavior:
- Biatec clears its local IdP session cookie.
- If `post_logout_redirect_uri` is provided and allowlisted, browser is redirected there.
- If `state` is provided, it is appended and returned in redirect query string.
- If `post_logout_redirect_uri` is provided, `client_id` (or `id_token_hint` containing `aud`) must resolve to a known client.

Allowlist matching rules:
- `post_logout_redirect_uri` must be absolute.
- Matching is performed on scheme + host + port + path.
- Query parameters are permitted when the base URI is allowlisted.
- Example: allowlisted `http://localhost:5173/login` accepts runtime `http://localhost:5173/login?redirect=%2F&oidc_retry=consent`.

### 3) Client registration requirements
For each Capitalism OIDC client (`capitalism`, `capitalism-master`):
- Register allowed login callbacks in `RedirectUris`.
- Register allowed logout callbacks in `PostLogoutRedirectUris`.

Minimum logout redirect URIs:
- `https://<game-host>/login`
- `https://<master-host>/login`

Local dev examples:
- `http://localhost:5173/login`
- `http://localhost:5174/login`

Compatibility note:
- If `PostLogoutRedirectUris` is empty, Biatec falls back to `RedirectUris` for logout redirect allowlist.

### 4) Session invalidation semantics
After logout at Biatec:
- The Biatec local authentication session is cleared.
- A new authorization request requires a fresh Biatec sign-in session.

## Login Behavior When Drive Consent Is Denied

- OIDC login still succeeds for `openid profile email`.
- Algorand-specific claim `algorand_address` is omitted until Drive consent is granted.
- Relying parties must treat `algorand_address` as optional and request additional permissions only before Drive-backed operations.

## Non-Goals (Current Scope)

Not required for this phase:
- Token revocation endpoint usage by frontend SPA.
- Back-channel logout to relying parties.

## Verification Checklist

1. Login via Biatec to game frontend.
2. Click Logout.
3. Confirm redirect to `end_session_endpoint`.
4. Confirm return redirect to `/login`.
5. Start login again.
6. Confirm Biatec requires fresh login session.
7. Repeat same flow for master frontend.

## Environment Variable Contract Used by Capitalism

Both frontends support:
- `VITE_BIATEC_OIDC_END_SESSION_URL`

Expected frontend behavior:
- If set and current provider is Biatec OIDC, frontend performs RP-initiated logout redirect.
- If not set, frontend clears only local app session.

Recommended value:
- `VITE_BIATEC_OIDC_END_SESSION_URL=https://google.biatec.io/connect/endsession`
