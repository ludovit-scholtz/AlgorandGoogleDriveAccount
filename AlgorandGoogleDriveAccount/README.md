# Biatec MCP Server - Algorand Self-Custody Google Drive Integration

A secure Model Context Protocol (MCP) server that enables AI assistants to interact with self-custody Algorand accounts stored encrypted in Google Drive.

## ?? True Self-Custody Architecture

**Your keys, your control:** This service provides genuine self-custody Algorand account management:

- **?? Your Google Drive Storage**: Private keys are encrypted and stored exclusively in your personal Google Drive
- **?? Email-Specific Encryption**: Keys are cryptographically bound to your email address and cannot be used by others  
- **?? Non-Transferable**: Encrypted keys cannot be moved between different Google Drive accounts
- **? Secure Processing**: Biatec servers process your encrypted keys only when you authorize transaction signing
- **??? No Custodial Risk**: We never store your unencrypted private keys on our servers

**Result**: Complete ownership and control of your Algorand assets while benefiting from AI-powered MCP integration.

## ?? Features

- **Self-Custody Account Storage**: Algorand private keys encrypted and stored in your personal Google Drive with email-specific binding
- **MCP Integration**: Compatible with Claude Desktop and Visual Studio Code for AI-assisted blockchain operations
- **Secure Device Pairing**: Cross-device synchronization with OAuth while maintaining self-custody
- **Email-Bound Security**: Enhanced security monitoring with cryptographic email binding  
- **Authorized Processing**: Server-side key processing only during explicitly authorized transaction signing

## ?? Self-Custody Security

- **True Self-Custody**: Private keys encrypted and stored exclusively in your Google Drive - never on our servers
- **Email-Specific Encryption**: AES-256 encryption with keys derived from your specific email address
- **Non-Transferable Design**: Cryptographic binding prevents keys from being used with different email addresses  
- **Authorized Processing Only**: Encrypted keys are processed on our servers only during transaction signing that you authorize
- **OAuth 2.0**: Secure Google authentication for device pairing and account access
- **CORS**: Configurable cross-origin resource sharing
- **Rate Limiting**: API call limits by service tier
- **Audit Logging**: Comprehensive security event logging for all key processing operations
- **UTF-8 Compliance**: All HTML pages properly encoded with UTF-8 for international character support
- **Cross-Account Protection**: Optional Google security monitoring (disabled by default, configurable)

## ?? Quick Start

### Prerequisites

- .NET 8.0 or later
- Redis server
- Google OAuth 2.0 credentials
- Google Drive API access

### Installation

1. Clone the repository:git clone <repository-url>
   cd AlgorandGoogleDriveAccount2. Configure settings in `appsettings.json`:{
  "App": {
    "Host": "https://your-domain.com",
    "ClientId": "your-google-client-id",
    "ClientSecret": "your-google-client-secret"
  },
  "Redis": {
       "ConnectionString": "localhost:6379"
     }
   }3. Run the application:dotnet run
## ?? Configuration & Setup Instructions

### Prerequisites Setup

Before connecting to the MCP server, you need:

1. **Active Internet Connection**: To access https://google.biatec.io/mcp/
2. **Google Account**: For authentication and Drive access
3. **Session ID**: A unique identifier for your session (you can generate one or use any unique string)

### ?? Claude Desktop Setup

Claude Desktop has built-in MCP support. Follow these steps to connect to the Biatec MCP Server:

#### Step 1: Locate Claude Desktop Configuration

1. **Windows**: Open `%APPDATA%\Claude\claude_desktop_config.json`
2. **macOS**: Open `~/Library/Application Support/Claude/claude_desktop_config.json`
3. **Linux**: Open `~/.config/Claude/claude_desktop_config.json`

If the file doesn't exist, create it.

#### Step 2: Add Biatec MCP Server Configuration

Add the following configuration to your `claude_desktop_config.json`:
{
  "mcpServers": {
    "biatec-algorand": {
      "command": "npx",
      "args": [
        "@modelcontextprotocol/server-fetch",
        "https://google.biatec.io/mcp/"
      ],
      "env": {
        "MCP_SERVER_URL": "https://google.biatec.io/mcp/",
        "SESSION_ID": "your-unique-session-id-here"
      }
    }
  }
}
**Important**: Replace `"your-unique-session-id-here"` with your own unique session ID (e.g., `"claude_session_123456"`)

#### Step 3: Restart Claude Desktop

1. Close Claude Desktop completely
2. Reopen Claude Desktop
3. The Biatec MCP Server should now be available

#### Step 4: Pair Your Device

1. Visit: `https://google.biatec.io/pair.html?session=your-unique-session-id-here`
2. Replace `your-unique-session-id-here` with the same Session ID from Step 2
3. Complete the Google OAuth authentication
4. Grant Google Drive permissions when prompted

#### Step 5: Test the Connection

In Claude Desktop, try asking:"Can you get my Algorand address using the Biatec MCP server?"
Claude should now be able to access your Algorand account information!

### ?? Visual Studio Code Setup

Visual Studio Code requires the MCP extension and configuration file.

#### Step 1: Install MCP Extension

1. Open Visual Studio Code
2. Go to Extensions (Ctrl+Shift+X)
3. Search for "Model Context Protocol" or "MCP"
4. Install the official MCP extension

#### Step 2: Create MCP Configuration File

Create a file named `mcp.json` in your project root or workspace folder:
{
  "mcp": {
    "servers": [
      {
        "name": "biatec-algorand",
        "url": "https://google.biatec.io/mcp/",
        "description": "Biatec Algorand Google Drive MCP Server",
        "sessionId": "your-unique-session-id-here",
        "headers": {
          "Content-Type": "application/json"
        }
      }
    ]
  }
}
**Important**: Replace `"your-unique-session-id-here"` with your own unique session ID (e.g., `"vscode_session_789012"`)

#### Step 3: Configure VS Code Settings

Open VS Code settings (`Ctrl+,`) and add:
{
  "mcp.configFile": "./mcp.json",
  "mcp.autoConnect": true
}
Or add to your workspace settings in `.vscode/settings.json`:
{
  "mcp.servers": [
    {
      "name": "biatec-algorand",
      "url": "https://google.biatec.io/mcp/",
      "sessionId": "your-unique-session-id-here"
    }
  ]
}
#### Step 4: Pair Your Device

1. Visit: `https://google.biatec.io/pair.html?session=your-unique-session-id-here`
2. Replace `your-unique-session-id-here` with the same Session ID from Step 2
3. Complete the Google OAuth authentication
4. Grant Google Drive permissions when prompted

#### Step 5: Test the Connection

1. Open Command Palette (`Ctrl+Shift+P`)
2. Run "MCP: Connect to Server"
3. Select "biatec-algorand"
4. Try using MCP tools in your workflow

### ?? Available MCP Tools

Once connected, you can use these tools:

- **`getAlgorandAddress`**: Retrieves your Algorand account address from encrypted Google Drive storage

### ?? Troubleshooting

#### Common Issues:

1. **"Session not found" error**:
   - Make sure you've completed device pairing at `https://google.biatec.io/pair.html?session=YOUR_SESSION_ID`
   - Verify your Session ID matches in both the configuration and pairing URL

2. **"Access token expired" error**:
   - Re-visit the pairing page to refresh your authentication
   - Ensure Google Drive permissions are granted

3. **"MCP server not responding" error**:
   - Check your internet connection
   - Verify the server URL: `https://google.biatec.io/mcp/`
   - Try restarting your IDE

#### Getting Help:

- **Technical Support**: support@biatec.io
- **Device Pairing Issues**: Visit https://google.biatec.io/pair.html
- **Documentation**: This README and inline help

### Google OAuth Setup (For Developers)

If you're setting up your own instance:

1. Go to [Google Cloud Console](https://console.cloud.google.com/)
2. Create a new project or select existing
3. Enable Google Drive API
4. Create OAuth 2.0 credentials
5. Add authorized redirect URIs:
   - `https://your-domain.com/signin-google`
   - `https://your-domain.com/api/device/paired-device`

## ?? Project Structure

AlgorandGoogleDriveAccount/
??? Controllers/           # API controllers
?   ??? DevicePairingController.cs
?   ??? DriveController.cs
??? BusinessLogic/         # Service layer
?   ??? DevicePairingService.cs
?   ??? DriveService.cs
?   ??? CrossAccountProtectionService.cs
??? Repository/            # Data access layer
?   ??? GoogleDriveRepository.cs
??? Model/                 # Data models
?   ??? Configuration.cs
??? MCP/                   # MCP server implementation
?   ??? BiatecMCPGoogle.cs
??? Helper/                # Utility classes
?   ??? AesEncryptionHelper.cs
??? wwwroot/               # Static web files
    ??? index.html
    ??? pair.html
    ??? privacy.html
    ??? terms.html

## ?? Security

- **Encryption**: All private keys encrypted with AES-256
- **OAuth 2.0**: Secure Google authentication
- **CORS**: Configurable cross-origin resource sharing
- **Rate Limiting**: API call limits by service tier
- **Audit Logging**: Comprehensive security event logging
- **UTF-8 Compliance**: All HTML pages properly encoded with UTF-8 for international character support
- **Cross-Account Protection**: Optional Google security monitoring (disabled by default, configurable)

### Cross-Account Protection Configuration

Cross-Account Protection is disabled by default but can be enabled through configuration:
{
  "CrossAccountProtection": {
    "Enabled": false,
    "RequireSecurityCheck": true,
    "SecurityCheckIntervalMinutes": 60,
    "AutoReportEvents": true,
    "EnableGranularConsent": false,
    "FilterInternalScopes": true
  }
}
**Configuration Options:**
- `Enabled`: Enable/disable Cross-Account Protection features (default: false)
- `RequireSecurityCheck`: Require security validation for account access (default: true)
- `SecurityCheckIntervalMinutes`: Interval between security checks (default: 60)
- `AutoReportEvents`: Automatically report security events (default: true)
- `EnableGranularConsent`: Enable Google's granular consent features (default: false)
- `FilterInternalScopes`: Filter internal Google scopes to prevent scope warnings (default: true)

## ?? Self-Custody Wealth Management Tiers

Service tiers are automatically determined based on your total self-custody Algorand portfolio value:

| Tier | Portfolio Value | Devices | Support | SLA | Features |
|------|----------------|---------|---------|-----|----------|
| Free | < �10,000 | 1 | Community | Best effort | Basic self-custody account management |
| Professional | �10,000 - �1,000,000 | 5 | Priority | 99.5% | Advanced self-custody features, portfolio analytics |
| Enterprise | > �1,000,000 | Unlimited | Dedicated | 99.9% | Premium self-custody features, custom integrations |

**Note:** No monthly fees - tier assignment is automatic based on your self-custody portfolio value calculated daily using real-time market prices. Your private keys remain in your Google Drive regardless of tier.

## ?? Deployment

### Docker Deployment
docker build -t biatec-mcp-server .
docker run -p 80:80 biatec-mcp-server
### Environment Variables

- `ASPNETCORE_ENVIRONMENT`: Development/Production
- `ConnectionStrings__Redis`: Redis connection string
- `App__ClientId`: Google OAuth client ID
- `App__ClientSecret`: Google OAuth client secret

## ?? API Documentation

Visit `/swagger` when running the application to access the interactive API documentation.

### Key Endpoints

- `GET /`: Main application page
- `GET /api/device/app`: Device pairing interface
- `GET /api/device/pair-device`: Initiate device pairing
- `GET /api/device/access-token/{sessionId}`: Get access token
- `GET /mcp`: MCP server endpoint

## ?? Contributing

1. Fork the repository
2. Create a feature branch
3. Make your changes
4. Add tests
5. Submit a pull request

## ?? Legal

- **Privacy Policy**: [/privacy.html](./wwwroot/privacy.html)
- **Terms of Service**: [/terms.html](./wwwroot/terms.html)
- **Company**: Scholtz & Company, j.s.a. (Slovakia)
- **Company ID**: 51882272
- **Tax ID**: 2120828105

## ?? Support

- **General**: support@biatec.io
- **Privacy**: privacy@biatec.io
- **Legal**: legal@biatec.io

## ?? License

This project is proprietary software owned by Scholtz & Company, j.s.a.

---

*Built with ?? for the Algorand ecosystem*


## Biatec JWT issuer

Whitelisted applications can use Biatec Google authentication as an identity provider and obtain signed JWT tokens with Algorand identity claims.

The implementation follows OpenID Connect style discovery and token endpoints while preserving compatibility with the legacy `returnUrl` flow.

### Endpoints

- `GET /.well-known/openid-configuration` - OIDC discovery metadata.
- `GET /.well-known/jwks.json` - public signing keys for JWT validation.
- `GET /authorize` - authorization endpoint.
  - standard mode: `response_type=code` and exchange at `/token`.
  - legacy mode: `returnUrl` alias with direct `id_token` form POST.
- `POST /token` - exchanges authorization code and also supports refresh token renewal.
- `GET /userinfo` - returns claims from access token.
- `POST /introspect` and `POST /verify` - token activity and verification helpers.

### Claims in issued tokens

- `email`
- `algorand_address`
- `preferred_username` and `name` set to the short identity derived from Algorand address (first 4 + last 4 chars)
- standard claims such as `sub`, `iss`, `aud`, `exp`, `iat`, `nbf`, `jti`

### Whitelisting and client registration

Clients and allowed redirect URLs are configured in `JwtIssuer:Clients` in `appsettings.json`. Redirect URI matching is exact and checked before any authorization response is returned.

### SigningPrivateKeyPem setup (secure and working)

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
2. File path in `JwtIssuer:SigningPrivateKeyPem` (the service now resolves existing file paths and reads the PEM file)

Example inline value:

```json
"JwtIssuer": {
  "KeyId": "biatec-main-key-2026-05",
  "SigningPrivateKeyPem": "-----BEGIN PRIVATE KEY-----\\nMIIEv...\\n-----END PRIVATE KEY-----"
}
```

### About Ed25519 / EdDSA

`Ed25519` JWT signing (`EdDSA`) is currently **not enabled** in this implementation because the active `System.IdentityModel.Tokens.Jwt` / `Microsoft.IdentityModel.Tokens` stack in this project does not expose EdDSA signing primitives in the current package set.

Supported algorithms in this service today:

- `RS256` (default)
- The library also supports ECDSA families (`ES256`, `ES384`, `ES512`), but this service is currently wired for `RS256` only.

### Detailed integration guide

See `OIDC_INTEGRATION_GUIDE.md` for full configuration, security guidance, and a Copilot-ready prompt for implementing this flow in destination projects.