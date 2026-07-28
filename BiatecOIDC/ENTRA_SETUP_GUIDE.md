# Setting up Microsoft Entra ID for Biatec

This guide covers registering the Biatec app in Microsoft Entra ID so users can sign in with a
Microsoft account and have their self-custody Algorand account stored in their **OneDrive app
folder** instead of Google Drive. It's the Microsoft-side counterpart to the Google Cloud OAuth
setup already required by `App:ClientId`/`App:ClientSecret`.

Both `BiatecMCP` (device pairing, `pair.html`) and `BiatecOIDC` (`/authorize`) use the same Entra
app registration - one `ClientId`/`ClientSecret` pair, shared via the `MicrosoftEntra` config
section in each service's `appsettings.json` (and, in production, the `google-account-main-app-secret`
Kubernetes secret - same convention as the existing Google `App:ClientId`/`ClientSecret`).

## Why `Files.ReadWrite.AppFolder`, not `Files.ReadWrite`

Google Drive access in this codebase uses the `drive.file` scope: the app can only see files it
itself created, never the rest of the user's Drive. The direct Microsoft Graph equivalent is the
delegated permission **`Files.ReadWrite.AppFolder`**, which confines the app to a special,
per-app folder (`/me/drive/special/approot`) that Microsoft manages - the app can never browse,
list, or read anything else in the user's OneDrive. This is the permission this codebase is built
around (see `BiatecSelfCustodyCore/Repository/OneDriveFileStore.cs`); do not substitute the
broader `Files.ReadWrite` (all files) permission.

## 1. Create the App Registration

1. Go to the [Microsoft Entra admin center](https://entra.microsoft.com/) → **Applications** →
   **App registrations** → **New registration**.
2. Name it (e.g. `Biatec`).
3. **Supported account types**: choose based on who should be able to sign in:
   - **Accounts in any organizational directory and personal Microsoft accounts** (multi-tenant +
     personal) if you want any Microsoft/Outlook.com/Live.com user to be able to sign in - this
     matches `TenantId: "common"` in config.
   - A single-tenant or org-only option if you want to restrict sign-in to a specific
     organization - use that tenant's ID (or `organizations`) as `MicrosoftEntra:TenantId` instead.
4. **Redirect URI** (platform: **Web**) - add all of these that apply to your deployment:
   - `https://google.biatec.io/signin-microsoft` (BiatecMCP, production)
   - `https://google.biatec.io/oidc/signin-microsoft` (BiatecOIDC, production - **note the
     different path** from BiatecMCP's, required so the callback lands on the right service; see
     `k8s/main/deployment-oidc.yaml`)
   - `https://localhost:7203/signin-microsoft` (BiatecMCP, local dev - adjust the port to match
     your `launchSettings.json`)
   - `https://localhost:7204/oidc/signin-microsoft` (BiatecOIDC, local dev)
5. Click **Register**.

## 2. Create a client secret

1. In the app registration, go to **Certificates & secrets** → **Client secrets** → **New client
   secret**.
2. Give it a description and expiry, then **Add**.
3. Copy the secret **value** immediately - it's only shown once. This is `MicrosoftEntra:ClientSecret`.

The **Application (client) ID** shown on the app's **Overview** page is `MicrosoftEntra:ClientId`.

## 3. Configure API permissions

1. Go to **API permissions** → **Add a permission** → **Microsoft Graph** → **Delegated
   permissions**.
2. Add:
   - `Files.ReadWrite.AppFolder` - the OneDrive app-folder scope described above.
   - `openid`, `profile`, `email` - identity claims (usually pre-added by default).
   - `offline_access` - so refresh tokens are issued (used the same way the Google refresh token
     is, for long-lived device pairing sessions).
3. `Files.ReadWrite.AppFolder` does not require tenant-admin consent for personal/multi-tenant
   sign-in - users consent for themselves on first sign-in. If you restricted the app to a single
   organization and that org enforces admin consent policies, click **Grant admin consent**.

## 4. Configure the app

Set these in each service's `appsettings.json` (or the equivalent environment
variables/Kubernetes secret in production - `MicrosoftEntra__TenantId`, `MicrosoftEntra__ClientId`,
`MicrosoftEntra__ClientSecret`):

```json
"MicrosoftEntra": {
  "TenantId": "common",
  "ClientId": "<Application (client) ID>",
  "ClientSecret": "<the client secret value>"
}
```

## How this plugs into the code

- `BiatecMCP/Program.cs` and `BiatecOIDC/Program.cs` each register a second authentication scheme,
  `AddOpenIdConnect(AuthSchemeNames.Microsoft, ...)`, alongside the existing Google one, pointed at
  `https://login.microsoftonline.com/{TenantId}/v2.0`.
- `BiatecSelfCustodyCore/Repository/OneDriveFileStore.cs` reads/writes the encrypted account file
  via plain Graph REST calls (`GET`/`PUT .../me/drive/special/approot:/{fileName}:/content`) using
  whatever bearer token the signed-in session has - no Microsoft.Graph SDK dependency.
- Which provider a session used is recorded as a `biatec_idp` claim (`"Google"` or `"Microsoft"`,
  see `BiatecSelfCustodyCore/Model/AuthSchemeNames.cs`) added during each scheme's
  `OnTokenValidated`, and persisted on `PairedDeviceInfo.Provider` for device-pairing sessions.
- The provider picker (`pair.html`'s two buttons, and `BiatecOIDC`'s `/select-provider` page) lets
  a user choose Google or Microsoft; a `?idp=google` or `?idp=microsoft` query parameter on
  `/authorize` or `/api/device/pair-device` skips the picker entirely (the "fast track").
- Before finalizing either flow, `BiatecSelfCustodyCore/BusinessLogic/StorageAccessVerifier.cs`
  confirms the fresh token actually has storage-write access (a user can decline just that specific
  permission on the consent screen); if it's missing, the browser is sent through one
  incremental-consent round-trip requesting `Files.ReadWrite.AppFolder` again with a forced
  consent screen before the OIDC code/token is issued.

## Troubleshooting

- **`AADSTS50011: redirect URI mismatch`**: the redirect URI Entra received doesn't exactly match
  one registered in step 1 (including scheme, host, path, and trailing slash). Double check
  `MicrosoftEntra:TenantId`/the deployed host, and that you registered both the `BiatecMCP` and
  `BiatecOIDC` redirect URIs (they differ by path).
- **Consent screen doesn't show `Files.ReadWrite.AppFolder`, or the app can't write after
  sign-in**: `StorageAccessVerifier` will detect this and trigger the one-time re-consent
  round-trip automatically; if it still fails after that, re-check step 3's permissions were
  actually added (not just requested) and that admin consent isn't silently blocking the scope in
  a restricted tenant.
- **Personal Microsoft accounts can't sign in**: check the app registration's "Supported account
  types" (step 3) actually includes personal accounts, and `MicrosoftEntra:TenantId` is `common`
  (not a specific tenant GUID, which restricts to that org only).
