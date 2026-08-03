using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using BiatecOIDC.BusinessLogic;
using BiatecOIDC.Helper;
using BiatecOIDC.Model;
using BiatecOIDC.Swagger;
using BiatecSelfCustodyCore.BusinessLogic;
using BiatecSelfCustodyCore.Model;
using BiatecSelfCustodyCore.Providers;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Caching.Distributed;

namespace BiatecOIDC.Controllers
{
    /// <summary>
    /// OpenID Connect identity provider: discovery/JWKS, the authorize/token endpoints (standard
    /// <c>response_type=code</c> with optional PKCE, plus a legacy <c>returnUrl</c> direct <c>id_token</c>
    /// flow), userinfo, token introspection/verification, and RP-Initiated Logout. Issues RS256-signed
    /// tokens carrying an identity claim (<c>primary_seed_address</c>) for whitelisted third-party
    /// clients registered under <c>JwtIssuer:Clients</c>.
    /// </summary>
    [ApiController]
    [Route("")]
    public class JwtIssuerController : ControllerBase
    {
        private const string AuthorizeAttemptPrefix = "oidc:authorize-attempts:";
        private const int MaxAuthorizeAttempts = 3;
        private static readonly TimeSpan AuthorizeAttemptWindow = TimeSpan.FromSeconds(10);

        private readonly IJwtIssuerService _jwtIssuerService;
        private readonly IDistributedCache _cache;
        private readonly ICloudStorageProviderCatalog _providerCatalog;

        public JwtIssuerController(IJwtIssuerService jwtIssuerService, IDistributedCache cache, ICloudStorageProviderCatalog providerCatalog)
        {
            _jwtIssuerService = jwtIssuerService;
            _cache = cache;
            _providerCatalog = providerCatalog;
        }

        /// <summary>
        /// OIDC discovery document. Includes <c>end_session_endpoint</c>; front/back-channel logout
        /// are not supported (both advertised as <c>false</c>).
        /// </summary>
        /// <returns>The OpenID Connect provider metadata document.</returns>
        [AllowAnonymous]
        [HttpGet(".well-known/openid-configuration")]
        public IActionResult OpenIdConfiguration()
        {
            return Ok(_jwtIssuerService.GetDiscoveryDocument(Request));
        }

        /// <summary>
        /// RFC 8414 OAuth 2.0 Authorization Server Metadata - identical content to
        /// <see cref="OpenIdConfiguration"/> (OIDC Discovery is a superset of RFC 8414's required fields, and
        /// extra fields are harmless per both specs). Exists because the MCP Authorization spec's
        /// "Authorization Server Metadata Discovery" only requires an authorization server to implement *one*
        /// of the two discovery mechanisms, but real-world MCP clients (e.g. VS Code's MCP client) probe this
        /// specific well-known URL first and only fall back to OIDC Discovery if it 404s - serving both here
        /// removes that warning entirely and is more broadly compatible with pure-OAuth (non-OIDC-aware)
        /// clients that only ever check this one.
        /// </summary>
        /// <returns>The same provider metadata document as <see cref="OpenIdConfiguration"/>.</returns>
        [AllowAnonymous]
        [HttpGet(".well-known/oauth-authorization-server")]
        public IActionResult OAuthAuthorizationServerMetadata()
        {
            return Ok(_jwtIssuerService.GetDiscoveryDocument(Request));
        }

        /// <summary>
        /// The RS256 public signing key(s) used to verify tokens issued by this provider.
        /// </summary>
        /// <returns>A JSON Web Key Set (JWKS).</returns>
        [AllowAnonymous]
        [HttpGet(".well-known/jwks.json")]
        public IActionResult Jwks()
        {
            return Ok(_jwtIssuerService.GetJsonWebKeySet());
        }

        /// <summary>
        /// RFC 7591 Dynamic Client Registration - public clients only (never issues a client secret). Lets
        /// an OAuth client that has no pre-arranged relationship with this issuer (e.g. an MCP client such
        /// as Claude Desktop, discovering this server via RFC 9728 Protected Resource Metadata) obtain a
        /// <c>client_id</c> at connect time instead of an operator hand-adding it to
        /// <c>JwtIssuer:Clients</c>. The registered client's scopes are always capped to
        /// <c>JwtIssuer:DynamicClientRegistrationDefaultScopes</c> (never <c>manage-limits</c>/<c>rekey</c>),
        /// regardless of what <paramref name="request"/> asks for - see
        /// <c>JwtIssuerConfiguration.DynamicClientRegistrationDefaultScopes</c>'s remarks.
        /// </summary>
        /// <response code="201">The newly-registered public client's credentials (there is no secret).</response>
        /// <response code="400">
        /// <c>redirect_uris</c> was empty, contained a non-HTTPS/non-loopback URI, or
        /// <c>token_endpoint_auth_method</c> requested anything other than <c>"none"</c>.
        /// </response>
        [AllowAnonymous]
        [EnableRateLimiting("client-registration")]
        [HttpPost("register")]
        public async Task<IActionResult> Register([FromBody] DynamicClientRegistrationRequest request)
        {
            if (!string.IsNullOrWhiteSpace(request.TokenEndpointAuthMethod) &&
                !string.Equals(request.TokenEndpointAuthMethod, "none", StringComparison.Ordinal))
            {
                return BadRequest(new ProblemDetails
                {
                    Title = "invalid_client_metadata",
                    Detail = "Only public clients (token_endpoint_auth_method=\"none\") can be registered here - no client secret is ever issued."
                });
            }

            try
            {
                var client = await _jwtIssuerService.RegisterDynamicClientAsync(request.ClientName, request.RedirectUris, request.Scope);
                var response = new DynamicClientRegistrationResponse
                {
                    ClientId = client.ClientId,
                    ClientIdIssuedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    RedirectUris = client.RedirectUris,
                    Scope = string.Join(' ', client.AllowedScopes)
                };
                return StatusCode(StatusCodes.Status201Created, response);
            }
            catch (ArgumentException ex)
            {
                return BadRequest(new ProblemDetails { Title = "invalid_client_metadata", Detail = ex.Message });
            }
        }

        /// <summary>
        /// OIDC authorization endpoint. Supports the standard <c>response_type=code</c> flow (exchange
        /// the returned code at <c>/token</c>, with optional PKCE via <paramref name="codeChallenge"/>/
        /// <paramref name="codeChallengeMethod"/> - required for public clients with no client secret),
        /// plus a legacy flow where <paramref name="returnUrl"/> receives the <c>id_token</c> directly.
        /// If the caller isn't already signed in, this redirects to Google sign-in first.
        /// </summary>
        /// <param name="clientId">Registered client identifier (<c>JwtIssuer:Clients</c>).</param>
        /// <param name="redirectUri">Allowlisted redirect URI for the standard code flow.</param>
        /// <param name="returnUrl">Allowlisted return URL for the legacy direct <c>id_token</c> flow.</param>
        /// <param name="responseType">Defaults to <c>code</c>.</param>
        /// <param name="responseMode">e.g. <c>query</c> (default) or <c>form_post</c>.</param>
        /// <param name="scope">Space-separated scopes; defaults to <c>openid profile email</c>.</param>
        /// <param name="state">Opaque value round-tripped back to the client.</param>
        /// <param name="nonce">Value round-tripped into the issued <c>id_token</c>.</param>
        /// <param name="codeChallenge">PKCE code challenge (RFC 7636); required for public clients.</param>
        /// <param name="codeChallengeMethod"><c>S256</c> (recommended) or <c>plain</c>.</param>
        /// <param name="resource">
        /// RFC 8707 resource indicator - the canonical URI of the resource server the requested token must
        /// be valid for (e.g. <c>https://mcp.biatec.io/mcp</c>). Must match an entry in
        /// <c>JwtIssuer:ProtectedResources</c>, and be repeated identically on <c>/token</c>.
        /// </param>
        /// <param name="idp">
        /// Fast track: <c>"google"</c> or <c>"microsoft"</c> skips the provider-picker page and challenges
        /// that provider directly. Omit to show the picker.
        /// </param>
        /// <returns>
        /// A redirect to the provider picker or straight to sign-in, to the client's redirect URI with the
        /// result, or an error.
        /// </returns>
        [AllowAnonymous]
        [HttpGet("authorize")]
        public async Task<IActionResult> Authorize(
            [FromQuery(Name = "client_id")] string? clientId,
            [FromQuery(Name = "redirect_uri")] string? redirectUri,
            [FromQuery(Name = "returnUrl")] string? returnUrl,
            [FromQuery(Name = "response_type")] string? responseType,
            [FromQuery(Name = "response_mode")] string? responseMode,
            [FromQuery(Name = "scope")] string? scope,
            [FromQuery(Name = "state")] string? state,
            [FromQuery(Name = "nonce")] string? nonce,
            [FromQuery(Name = "code_challenge")] string? codeChallenge,
            [FromQuery(Name = "code_challenge_method")] string? codeChallengeMethod,
            [FromQuery(Name = "resource")] string? resource,
            [FromQuery(Name = "idp")] string? idp)
        {
            var authRequest = new OidcAuthorizeRequest
            {
                ClientId = clientId,
                RedirectUri = redirectUri,
                ReturnUrl = returnUrl,
                ResponseType = responseType ?? "code",
                ResponseMode = responseMode,
                Scope = scope ?? "openid profile email",
                State = state,
                Nonce = nonce,
                CodeChallenge = codeChallenge,
                CodeChallengeMethod = codeChallengeMethod,
                Resource = resource
            };

            var validation = await _jwtIssuerService.ValidateAuthorizeRequestAsync(authRequest);
            if (!validation.IsValid || validation.NormalizedRequest == null || validation.Client == null)
            {
                return BuildAuthorizeErrorResponse(authRequest.RedirectUri ?? authRequest.ReturnUrl, authRequest.State, validation.Error ?? "invalid_request", validation.ErrorDescription ?? "Invalid request.", authRequest.ResponseMode);
            }

            var normalizedRequest = validation.NormalizedRequest;

            if (User.Identity?.IsAuthenticated != true)
            {
                if (!await TryRegisterAuthorizeAttemptAsync(normalizedRequest))
                {
                    return BuildAuthorizeErrorResponse(
                        normalizedRequest.RedirectUri,
                        normalizedRequest.State,
                        "temporarily_unavailable",
                        "Too many authorization attempts. Wait a few seconds before trying again.",
                        normalizedRequest.ResponseMode);
                }

                var requestId = await _jwtIssuerService.StorePendingAuthorizeRequestAsync(normalizedRequest);

                if (!string.IsNullOrWhiteSpace(idp))
                {
                    return AuthorizeChallenge(requestId, idp!, retried: false);
                }

                return RedirectToAction(nameof(SelectProvider), new { requestId });
            }

            return await RedirectToConsentAsync(normalizedRequest);
        }

        /// <summary>
        /// Provider picker shown when <c>/authorize</c> is called with no <c>idp</c> fast-track parameter
        /// and the caller isn't already signed in. Not part of the public OIDC contract. Styled to match
        /// <c>wwwroot/index.html</c>'s theme; only lists providers whose <see cref="ICloudStorageProvider.IsConfigured"/>
        /// is true, so an app registration that's missing its client id/secret in this environment never
        /// renders a sign-in button that can't work.
        /// </summary>
        /// <param name="requestId">Opaque id of the pending authorize request stored by <c>/authorize</c>.</param>
        [AllowAnonymous]
        [HttpGet("select-provider")]
        public async Task<IActionResult> SelectProvider([FromQuery] string requestId)
        {
            var configuredProviders = _providerCatalog.All.Where(p => p.IsConfigured).ToList();

            // Peek, not consume - AuthorizeChallenge/AuthorizeCallback still need the pending request
            // after the user picks a provider. Only used here to show the user who's asking (app name)
            // and what for (requested scopes) - never to make an authorization decision.
            var pending = await _jwtIssuerService.PeekPendingAuthorizeRequestAsync(requestId);
            var appName = ResolveAppName(pending?.ClientId, await ResolveClientConfigAsync(pending?.ClientId));
            var requestedScopes = (pending?.Scope ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries);

            var buttonsHtml = new StringBuilder();
            foreach (var provider in configuredProviders)
            {
                var url = Url.Action(nameof(AuthorizeChallenge), "JwtIssuer", new { requestId, idp = provider.Name }, Request.Scheme);
                buttonsHtml.Append($"""
                    <a class="provider-button" href="{WebUtility.HtmlEncode(url)}">
                        <span class="provider-icon">{GetProviderIconSvg(provider.Name)}</span>
                        <span class="provider-label">Continue with {WebUtility.HtmlEncode(provider.DisplayName)}</span>
                        <span class="provider-arrow">&rarr;</span>
                    </a>
                    """);
            }

            var bodyHtml = configuredProviders.Count > 0
                ? $"""<div class="provider-list">{buttonsHtml}</div>"""
                : """
                    <div class="callout">
                        <span class="callout-icon">&#9888;&#65039;</span>
                        <p><strong>No identity provider is configured.</strong> Please contact Biatec support.</p>
                    </div>
                    """;

            // Requested-scopes summary, so the user knows what they're about to grant before they even
            // pick a cloud provider - mirrors the detail shown on the consent screen further along, just
            // earlier in the flow. Omitted entirely if the pending request somehow has no scopes.
            var scopesHtml = requestedScopes.Length > 0
                ? $"""
                    <div class="requested-scopes">
                        <span class="requested-scopes-label">{WebUtility.HtmlEncode(appName)} is requesting:</span>
                        <ul>
                            {string.Join("\n", requestedScopes.Select(s => $"<li>{WebUtility.HtmlEncode(FriendlyScopeName(s))}</li>"))}
                        </ul>
                    </div>
                    """
                : string.Empty;

            var html = $"""
                <!DOCTYPE html>
                <html lang="en">
                <head>
                    <meta charset="UTF-8">
                    <meta name="viewport" content="width=device-width, initial-scale=1.0">
                    <title>Sign in to {WebUtility.HtmlEncode(appName)}</title>
                    <link rel="icon" href="/logo-biatec.png">
                    <link rel="preconnect" href="https://fonts.googleapis.com">
                    <link rel="preconnect" href="https://fonts.gstatic.com" crossorigin>
                    <link href="https://fonts.googleapis.com/css2?family=Space+Grotesk:wght@400;500;600;700&family=JetBrains+Mono:wght@400;500;600&display=swap" rel="stylesheet">
                    <style>{SelectProviderStyles}</style>
                </head>
                <body>
                    <div class="aurora"></div>
                    <div class="grid-overlay"></div>
                    <div class="picker-shell">
                        <div class="picker-card">
                            <img class="picker-logo" src="/logo-biatec.png" alt="Biatec logo">
                            <div class="eyebrow"><span class="dot"></span> Self-custody sign-in</div>
                            <h1>Sign in to <span class="glow-text">{WebUtility.HtmlEncode(appName)}</span></h1>
                            <p class="subtitle">Choose the cloud account that holds your self-custody Algorand key. Your key never leaves it.</p>
                            {scopesHtml}
                            {bodyHtml}
                        </div>
                    </div>
                </body>
                </html>
                """;

            return Content(html, "text/html; charset=utf-8", Encoding.UTF8);
        }

        /// <summary>
        /// Inline brand glyph for a known provider's picker button (<see cref="GoogleCloudStorageProvider.ProviderName"/>,
        /// <see cref="MicrosoftCloudStorageProvider.ProviderName"/>), falling back to a generic cloud glyph
        /// for any provider added later that doesn't have a dedicated icon yet.
        /// </summary>
        private static string GetProviderIconSvg(string providerName)
        {
            if (string.Equals(providerName, GoogleCloudStorageProvider.ProviderName, StringComparison.OrdinalIgnoreCase))
            {
                return """
                    <svg viewBox="0 0 24 24" width="24" height="24" aria-hidden="true">
                        <path fill="#4285F4" d="M23.52 12.27c0-.79-.07-1.54-.19-2.27H12v4.51h6.44c-.28 1.48-1.13 2.73-2.4 3.58v3h3.86c2.26-2.09 3.62-5.17 3.62-8.82z"/>
                        <path fill="#34A853" d="M12 24c3.24 0 5.95-1.08 7.93-2.91l-3.86-3c-1.08.72-2.45 1.16-4.07 1.16-3.13 0-5.78-2.11-6.73-4.96H1.28v3.09C3.26 21.3 7.31 24 12 24z"/>
                        <path fill="#FBBC05" d="M5.27 14.29c-.25-.72-.38-1.49-.38-2.29s.14-1.57.38-2.29V6.62H1.28C.47 8.24 0 10.06 0 12s.47 3.76 1.28 5.38l3.99-3.09z"/>
                        <path fill="#EA4335" d="M12 4.75c1.77 0 3.35.61 4.6 1.8l3.42-3.42C17.95 1.19 15.24 0 12 0 7.31 0 3.26 2.7 1.28 6.62l3.99 3.09C6.22 6.86 8.87 4.75 12 4.75z"/>
                    </svg>
                    """;
            }

            if (string.Equals(providerName, MicrosoftCloudStorageProvider.ProviderName, StringComparison.OrdinalIgnoreCase))
            {
                return """
                    <svg viewBox="0 0 23 23" width="24" height="24" aria-hidden="true">
                        <rect x="1" y="1" width="10" height="10" fill="#F25022"/>
                        <rect x="12" y="1" width="10" height="10" fill="#7FBA00"/>
                        <rect x="1" y="12" width="10" height="10" fill="#00A4EF"/>
                        <rect x="12" y="12" width="10" height="10" fill="#FFB900"/>
                    </svg>
                    """;
            }

            // Generic placeholder for provider #3+ until it gets its own brand glyph.
            return """
                <svg viewBox="0 0 24 24" width="24" height="24" fill="none" stroke="currentColor" stroke-width="2" aria-hidden="true">
                    <path d="M17.5 19H6.5a4.5 4.5 0 0 1-.99-8.89 5.5 5.5 0 0 1 10.68-1.87A4.5 4.5 0 0 1 17.5 19z"/>
                </svg>
                """;
        }

        /// <summary>Green checkmark glyph for a granted permission row on the consent screen.</summary>
        private const string CheckIconSvg = """
            <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.5" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><polyline points="20 6 9 17 4 12"></polyline></svg>
            """;

        /// <summary>Red cross glyph for a denied/not-requested permission row on the consent screen.</summary>
        private const string CrossIconSvg = """
            <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.5" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><line x1="18" y1="6" x2="6" y2="18"></line><line x1="6" y1="6" x2="18" y2="18"></line></svg>
            """;

        /// <summary>Danger triangle glyph for the <c>rekey</c> permission row on the consent screen - the single most dangerous scope Biatec issues.</summary>
        private const string WarningIconSvg = """
            <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.5" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="M10.29 3.86 1.82 18a2 2 0 0 0 1.71 3h16.94a2 2 0 0 0 1.71-3L13.71 3.86a2 2 0 0 0-3.42 0Z"></path><line x1="12" y1="9" x2="12" y2="13"></line><line x1="12" y1="17" x2="12.01" y2="17"></line></svg>
            """;

        private const string SelectProviderStyles = """
            :root {
                --bg-0: #05060d;
                --bg-1: #0a0d1a;
                --bg-2: #10142a;
                --panel: rgba(18, 22, 42, 0.6);
                --panel-border: rgba(140, 160, 255, 0.14);
                --panel-border-hover: rgba(140, 200, 255, 0.35);
                --text-0: #eef1fb;
                --text-1: #aab1cf;
                --text-2: #6d759a;
                --accent-cyan: #4de3ff;
                --accent-violet: #9b7bff;
                --accent-green: #35e6a4;
                --accent-amber: #ffb84d;
                --accent-red: #ff5470;
                --font-display: 'Space Grotesk', 'Segoe UI', system-ui, sans-serif;
                --font-mono: 'JetBrains Mono', 'SFMono-Regular', Consolas, monospace;
            }
            * { margin: 0; padding: 0; box-sizing: border-box; }
            body {
                font-family: var(--font-display);
                color: var(--text-0);
                background: var(--bg-0);
                min-height: 100vh;
                display: flex;
                align-items: center;
                justify-content: center;
                overflow-x: hidden;
                line-height: 1.6;
            }
            .aurora {
                position: fixed;
                inset: 0;
                z-index: -2;
                background:
                    radial-gradient(60vw 60vw at 12% 8%, rgba(77, 227, 255, 0.16), transparent 60%),
                    radial-gradient(55vw 55vw at 88% 18%, rgba(155, 123, 255, 0.18), transparent 60%),
                    radial-gradient(70vw 70vw at 50% 100%, rgba(53, 230, 164, 0.10), transparent 60%),
                    linear-gradient(180deg, var(--bg-0) 0%, var(--bg-1) 45%, var(--bg-2) 100%);
            }
            .grid-overlay {
                position: fixed;
                inset: 0;
                z-index: -1;
                background-image:
                    linear-gradient(rgba(140, 160, 255, 0.055) 1px, transparent 1px),
                    linear-gradient(90deg, rgba(140, 160, 255, 0.055) 1px, transparent 1px);
                background-size: 44px 44px;
                mask-image: radial-gradient(ellipse 80% 60% at 50% 0%, black 30%, transparent 90%);
            }
            .glow-text {
                background: linear-gradient(90deg, var(--accent-cyan), var(--accent-violet) 55%, var(--accent-amber));
                -webkit-background-clip: text;
                background-clip: text;
                color: transparent;
            }
            .picker-shell { width: 100%; padding: 2rem; }
            .picker-card {
                max-width: 440px;
                margin: 0 auto;
                background: var(--panel);
                border: 1px solid var(--panel-border);
                border-radius: 22px;
                padding: 2.5rem 2.25rem;
                text-align: center;
                backdrop-filter: blur(10px);
                box-shadow: 0 30px 80px rgba(5, 6, 13, 0.55);
            }
            .picker-logo {
                height: 76px;
                width: auto;
                margin-bottom: 1.75rem;
                filter: drop-shadow(0 0 22px rgba(77, 227, 255, 0.35));
            }
            .eyebrow {
                display: inline-flex;
                align-items: center;
                gap: 0.5rem;
                padding: 0.35rem 0.9rem;
                border-radius: 999px;
                border: 1px solid var(--panel-border);
                font-family: var(--font-mono);
                font-size: 0.72rem;
                color: var(--accent-cyan);
                letter-spacing: 0.06em;
                text-transform: uppercase;
                margin-bottom: 1.25rem;
            }
            .eyebrow .dot {
                width: 6px;
                height: 6px;
                border-radius: 50%;
                background: var(--accent-green);
                box-shadow: 0 0 10px var(--accent-green);
            }
            h1 { font-size: 1.7rem; font-weight: 700; letter-spacing: -0.01em; margin-bottom: 0.75rem; }
            .subtitle { font-size: 0.92rem; color: var(--text-1); margin-bottom: 2rem; }
            .requested-scopes {
                background: rgba(255, 255, 255, 0.03);
                border: 1px solid var(--panel-border);
                border-radius: 14px;
                padding: 0.9rem 1.1rem;
                margin-bottom: 1.5rem;
                text-align: left;
            }
            .requested-scopes-label {
                display: block;
                font-size: 0.78rem;
                font-weight: 600;
                color: var(--text-1);
                margin-bottom: 0.5rem;
            }
            .requested-scopes ul { list-style: none; display: flex; flex-direction: column; gap: 0.35rem; }
            .requested-scopes li {
                font-size: 0.85rem;
                color: var(--text-0);
                padding-left: 1.1rem;
                position: relative;
            }
            .requested-scopes li::before {
                content: "";
                position: absolute;
                left: 0;
                top: 0.5em;
                width: 5px;
                height: 5px;
                border-radius: 50%;
                background: var(--accent-cyan);
            }
            .provider-list { display: flex; flex-direction: column; gap: 0.9rem; }
            .provider-button {
                display: flex;
                align-items: center;
                gap: 0.9rem;
                padding: 0.9rem 1.1rem;
                background: rgba(255, 255, 255, 0.03);
                border: 1px solid var(--panel-border);
                border-radius: 14px;
                text-decoration: none;
                color: var(--text-0);
                font-weight: 600;
                font-size: 0.95rem;
                transition: transform 0.2s ease, border-color 0.2s ease, box-shadow 0.2s ease, background 0.2s ease;
            }
            .provider-button:hover {
                transform: translateY(-2px);
                border-color: var(--panel-border-hover);
                background: rgba(255, 255, 255, 0.06);
                box-shadow: 0 14px 34px rgba(8, 10, 24, 0.5);
            }
            .provider-icon {
                flex-shrink: 0;
                width: 36px;
                height: 36px;
                display: flex;
                align-items: center;
                justify-content: center;
                border-radius: 10px;
                background: rgba(255, 255, 255, 0.06);
            }
            .provider-label { flex: 1; text-align: left; }
            .provider-arrow { color: var(--text-2); font-family: var(--font-mono); }
            .provider-button:hover .provider-arrow { color: var(--accent-cyan); }
            .callout {
                display: flex;
                gap: 0.75rem;
                align-items: flex-start;
                background: linear-gradient(180deg, rgba(255, 184, 77, 0.1), transparent);
                border: 1px solid rgba(255, 184, 77, 0.3);
                border-radius: 14px;
                padding: 1rem 1.1rem;
                text-align: left;
            }
            .callout p { color: var(--text-1); font-size: 0.88rem; }
            .callout strong { color: var(--text-0); }

            /* ---------- Consent screen (/authorize/consent) ---------- */
            /* Sized to fit a full-HD (1920x1080) viewport without scrolling: tighter paddings/gaps and a
               smaller claims/permissions text than the provider-picker screen's. On wide screens the card
               widens instead of the content stacking taller, via the min-width rule below. */
            .consent-shell { padding: 1rem 2rem; }
            .consent-card { padding: 1.5rem 2rem; }
            .consent-card .picker-logo { height: 32px; margin-bottom: 1rem; }
            .consent-card .eyebrow { margin-bottom: 0.9rem; }
            .consent-card h1 { font-size: 1.35rem; margin-bottom: 0.4rem; }
            .consent-card .subtitle { font-size: 0.85rem; margin-bottom: 1.1rem; }
            .permission-list { display: flex; flex-direction: column; gap: 0.55rem; margin-bottom: 1rem; text-align: left; }
            .permission-row {
                display: flex;
                align-items: flex-start;
                gap: 0.75rem;
                padding: 0.6rem 0.85rem;
                background: rgba(255, 255, 255, 0.03);
                border: 1px solid var(--panel-border);
                border-radius: 12px;
            }
            .permission-icon {
                flex-shrink: 0;
                width: 22px;
                height: 22px;
                display: flex;
                align-items: center;
                justify-content: center;
                border-radius: 50%;
            }
            .permission-icon.granted { background: rgba(53, 230, 164, 0.16); color: var(--accent-green); }
            .permission-icon.denied { background: rgba(255, 84, 112, 0.14); color: var(--accent-red); }
            .permission-icon.danger { background: rgba(255, 84, 112, 0.28); color: var(--accent-red); }
            .permission-icon svg { width: 13px; height: 13px; }
            .permission-text { display: flex; flex-direction: column; gap: 0.1rem; }
            .permission-text strong { color: var(--text-0); font-size: 0.82rem; }
            .permission-text span { color: var(--text-1); font-size: 0.72rem; }
            .consent-warning { border-color: rgba(255, 184, 77, 0.3); }
            .consent-safe { border-color: rgba(53, 230, 164, 0.3); background: linear-gradient(180deg, rgba(53, 230, 164, 0.08), transparent); }
            .consent-danger { border-color: rgba(255, 84, 112, 0.6); background: linear-gradient(180deg, rgba(255, 84, 112, 0.16), transparent); }
            .consent-danger p { color: var(--text-0); }
            .consent-danger strong { color: var(--accent-red); }
            .consent-card .callout { padding: 0.7rem 0.85rem; }
            .consent-card .callout p { font-size: 0.78rem; }
            .consent-continue-btn {
                display: block;
                margin-top: 1.25rem;
                padding: 0.9rem 1.1rem;
                border-radius: 14px;
                text-align: center;
                text-decoration: none;
                font-weight: 600;
                font-size: 0.95rem;
                background: linear-gradient(90deg, var(--accent-cyan), var(--accent-violet));
                color: #05060d;
                transition: transform 0.2s ease, box-shadow 0.2s ease;
            }
            .consent-card .consent-continue-btn { margin-top: 0.85rem; padding: 0.7rem 1rem; font-size: 0.88rem; }
            .consent-continue-btn:hover { transform: translateY(-2px); box-shadow: 0 14px 34px rgba(8, 10, 24, 0.5); }
            .consent-continue-btn.secondary {
                background: rgba(255, 255, 255, 0.04);
                color: var(--text-0);
                border: 1px solid var(--panel-border);
            }
            .consent-auto-continue-row { display: flex; align-items: center; gap: 0.6rem; margin-top: 0.85rem; }
            .consent-auto-continue-row .consent-continue-btn { flex: 1; margin-top: 0; }
            .consent-pause-btn {
                flex-shrink: 0;
                padding: 0.7rem 0.9rem;
                border-radius: 14px;
                background: rgba(255, 255, 255, 0.04);
                color: var(--text-1);
                border: 1px solid var(--panel-border);
                font-family: var(--font-display);
                font-weight: 600;
                font-size: 0.82rem;
                cursor: pointer;
                transition: transform 0.2s ease, border-color 0.2s ease, color 0.2s ease;
            }
            .consent-pause-btn:hover { border-color: var(--panel-border-hover); color: var(--text-0); }

            /* Wide screens: let the consent card spread horizontally (permission text on one line, less
               vertical stacking) instead of staying pinned to the picker's narrow 440px width. */
            @media (min-width: 720px) {
                .consent-card { max-width: 620px; }
                .permission-row { align-items: center; }
            }
            @media (min-width: 1100px) {
                .consent-card { max-width: 720px; }
            }
            """;

        /// <summary>
        /// Challenges the chosen provider's sign-in. Reached either directly (the <c>idp</c> fast track on
        /// <c>/authorize</c>) or via a <see cref="SelectProvider"/> button click.
        /// </summary>
        /// <param name="requestId">Opaque id of the pending authorize request.</param>
        /// <param name="idp"><c>"google"</c> or <c>"microsoft"</c>.</param>
        /// <param name="retried">
        /// Set when re-challenging after <see cref="AuthorizeCallback"/> found storage-write access was
        /// missing - forces a fresh consent screen requesting that scope again.
        /// </param>
        [AllowAnonymous]
        [HttpGet("authorize/challenge")]
        public IActionResult AuthorizeChallenge([FromQuery] string requestId, [FromQuery] string idp, [FromQuery] bool retried = false)
        {
            var provider = _providerCatalog.Resolve(idp);
            var callbackUrl = Url.Action(nameof(AuthorizeCallback), "JwtIssuer", new { requestId, retried }, Request.Scheme);

            var properties = new AuthenticationProperties
            {
                RedirectUri = callbackUrl
            };

            if (retried)
            {
                properties.Items[OpenIdConnectIncrementalAuth.ForceConsentKey] = "true";
                properties.Items[OpenIdConnectIncrementalAuth.IncrementalScopesKey] = provider.RequiredScope;
            }

            return Challenge(properties, provider.Name);
        }

        /// <summary>
        /// Callback used internally after the user completes sign-in from <c>/authorize</c>. Not part of
        /// the public OIDC contract - resumes the pending authorize request identified by
        /// <paramref name="requestId"/> and finalizes it as if <c>/authorize</c> had been called while
        /// already signed in. Before finalizing, verifies the fresh token actually has storage-write
        /// access (the user can decline just that permission on the consent screen) and, if missing,
        /// sends the browser through one incremental-consent round-trip via <see cref="AuthorizeChallenge"/>
        /// so the OIDC code/token is never issued against a session that can't read/write the self-custody
        /// account file.
        /// </summary>
        /// <param name="requestId">Opaque id of the pending authorize request stored by <c>/authorize</c>.</param>
        /// <param name="retried">Internal guard - set after one incremental-consent round-trip, to avoid looping.</param>
        /// <returns>A redirect to the client's redirect URI with the result, or an error.</returns>
        [Authorize]
        [HttpGet("authorize/callback")]
        public async Task<IActionResult> AuthorizeCallback([FromQuery] string requestId, [FromQuery] bool retried = false)
        {
            if (string.IsNullOrWhiteSpace(requestId))
            {
                return BadRequest(new ProblemDetails { Detail = "Missing requestId." });
            }

            var pending = await _jwtIssuerService.GetPendingAuthorizeRequestAsync(requestId);
            if (pending == null)
            {
                return BadRequest(new ProblemDetails { Detail = "Authorization request not found or expired." });
            }

            await _jwtIssuerService.RemovePendingAuthorizeRequestAsync(requestId);

            var validation = await _jwtIssuerService.ValidateAuthorizeRequestAsync(pending);
            if (!validation.IsValid || validation.NormalizedRequest == null || validation.Client == null)
            {
                return BuildAuthorizeErrorResponse(
                    pending.RedirectUri ?? pending.ReturnUrl,
                    pending.State,
                    validation.Error ?? "invalid_request",
                    validation.ErrorDescription ?? "Invalid request.",
                    pending.ResponseMode);
            }

            if (!retried)
            {
                var provider = _providerCatalog.Resolve(User.FindFirst(AuthSchemeNames.IdpClaimType)?.Value);
                var accessToken = await HttpContext.GetTokenAsync(provider.Name, "access_token");
                if (!string.IsNullOrEmpty(accessToken) && !await provider.HasWriteAccessAsync(accessToken))
                {
                    var retryRequestId = await _jwtIssuerService.StorePendingAuthorizeRequestAsync(validation.NormalizedRequest);
                    return AuthorizeChallenge(retryRequestId, provider.Name, retried: true);
                }
            }

            return await RedirectToConsentAsync(validation.NormalizedRequest);
        }

        /// <summary>
        /// Stores <paramref name="request"/> as a pending authorize request and redirects to the consent
        /// screen (<see cref="AuthorizeConsent"/>) instead of finalizing immediately - every path that's
        /// about to issue a code/token (fresh sign-in via <see cref="AuthorizeCallback"/>, or an
        /// already-signed-in caller hitting <see cref="Authorize"/> directly) goes through this so the
        /// user always sees what the client is requesting before anything is issued.
        /// </summary>
        private async Task<IActionResult> RedirectToConsentAsync(OidcAuthorizeRequest request)
        {
            // The user is authenticated at this point (sign-in succeeded, or they already had a
            // session), so the rate limit's purpose - blocking repeated pre-auth attempts - no longer
            // applies; clear it now rather than waiting for the consent step, which the user may abandon.
            await ClearAuthorizeAttemptsAsync(request);

            var requestId = await _jwtIssuerService.StorePendingAuthorizeRequestAsync(request);
            return RedirectToAction(nameof(AuthorizeConsent), new { requestId });
        }

        /// <summary>
        /// Consent screen shown after sign-in and before a code/token is issued. Not part of the public
        /// OIDC contract. Always shows that identity will be verified; shows a granted/denied row for the
        /// <c>sign</c>, <c>manage-limits</c>, and <c>rekey</c> wallet scopes based on whether the client
        /// requested them (<c>rekey</c> additionally gets its own separate, more alarming danger callout -
        /// see <see cref="BuildConsentHtml"/> - since it's the one scope that risks total, irreversible
        /// loss of funds rather than spend bounded by a limit);
        /// and shows a granted/denied row for storage write access to the user's own Google Drive/OneDrive
        /// app-only folder, which is what actually holds the encrypted self-custody account file (separate
        /// from anything the OIDC client itself requested). If any of those three is missing/denied, the
        /// user must explicitly click through (no auto-continue) since either real account privileges or
        /// the storage this app depends on are at stake; otherwise the screen auto-continues after 5
        /// seconds (with a manual "Continue now" escape hatch) since there's nothing to confirm.
        /// </summary>
        /// <param name="requestId">Opaque id of the pending authorize request stored by <c>/authorize</c>.</param>
        [Authorize]
        [HttpGet("authorize/consent")]
        public async Task<IActionResult> AuthorizeConsent([FromQuery] string requestId)
        {
            if (string.IsNullOrWhiteSpace(requestId))
            {
                return BadRequest(new ProblemDetails { Detail = "Missing requestId." });
            }

            // Peek, not consume - the pending request must still be there for AuthorizeConsentContinue
            // to finalize, whether the user clicks through immediately or the auto-continue timer fires.
            var pending = await _jwtIssuerService.PeekPendingAuthorizeRequestAsync(requestId);
            if (pending == null)
            {
                return BadRequest(new ProblemDetails { Detail = "Authorization request not found or expired." });
            }

            var scopes = (pending.Scope ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var wantsSign = scopes.Contains("sign", StringComparer.Ordinal);
            var wantsLimits = scopes.Contains("manage-limits", StringComparer.Ordinal);
            var wantsRekey = scopes.Contains("rekey", StringComparer.Ordinal);

            // Same provider/token/write-access check AuthorizeCallback already does before its own
            // automatic one-shot incremental-consent retry - re-checked here so the result is visible on
            // the consent screen too, and so the user can retry manually beyond that single automatic
            // attempt instead of the flow just silently proceeding without storage access.
            var provider = _providerCatalog.Resolve(User.FindFirst(AuthSchemeNames.IdpClaimType)?.Value);
            var accessToken = await HttpContext.GetTokenAsync(provider.Name, "access_token");
            var hasStorageAccess = !string.IsNullOrEmpty(accessToken) && await provider.HasWriteAccessAsync(accessToken);

            var continueUrl = Url.Action(nameof(AuthorizeConsentContinue), "JwtIssuer", new { requestId }, Request.Scheme)!;
            var reAuthUrl = Url.Action(nameof(AuthorizeChallenge), "JwtIssuer", new { requestId, idp = provider.Name, retried = true }, Request.Scheme)!;
            var appName = ResolveAppName(pending.ClientId, await ResolveClientConfigAsync(pending.ClientId));

            return Content(
                BuildConsentHtml(appName, wantsSign, wantsLimits, wantsRekey, hasStorageAccess, provider.DisplayName, continueUrl, reAuthUrl),
                "text/html; charset=utf-8",
                Encoding.UTF8);
        }

        /// <summary>
        /// Finalizes the pending authorize request after the user has seen (and, if it requested
        /// sensitive scopes, confirmed) the consent screen. Reached either by the auto-continue timer or
        /// the "Continue" button on <see cref="AuthorizeConsent"/>.
        /// </summary>
        /// <param name="requestId">Opaque id of the pending authorize request.</param>
        [Authorize]
        [HttpGet("authorize/consent/continue")]
        public async Task<IActionResult> AuthorizeConsentContinue([FromQuery] string requestId)
        {
            if (string.IsNullOrWhiteSpace(requestId))
            {
                return BadRequest(new ProblemDetails { Detail = "Missing requestId." });
            }

            var pending = await _jwtIssuerService.GetPendingAuthorizeRequestAsync(requestId);
            if (pending == null)
            {
                return BadRequest(new ProblemDetails { Detail = "Authorization request not found or expired." });
            }

            var validation = await _jwtIssuerService.ValidateAuthorizeRequestAsync(pending);
            if (!validation.IsValid || validation.NormalizedRequest == null || validation.Client == null)
            {
                return BuildAuthorizeErrorResponse(
                    pending.RedirectUri ?? pending.ReturnUrl,
                    pending.State,
                    validation.Error ?? "invalid_request",
                    validation.ErrorDescription ?? "Invalid request.",
                    pending.ResponseMode);
            }

            return await FinalizeAuthorizeAsync(validation.NormalizedRequest, validation.Client);
        }

        /// <summary>
        /// Looks up <paramref name="clientId"/>'s registered configuration - statically-configured
        /// (<c>JwtIssuer:Clients</c>) or dynamically-registered (<c>POST /register</c>), via
        /// <see cref="IJwtIssuerService.ResolveClientAsync"/> - if any. Used by
        /// <see cref="SelectProvider"/>/<see cref="AuthorizeConsent"/> to resolve a human-friendly app name
        /// instead of showing the raw <c>client_id</c> to the user.
        /// </summary>
        private Task<JwtIssuerClientConfiguration?> ResolveClientConfigAsync(string? clientId) => _jwtIssuerService.ResolveClientAsync(clientId);

        /// <summary>
        /// The name shown to the user for a signing-in app: <see cref="JwtIssuerClientConfiguration.DisplayName"/>
        /// when the client has one configured, otherwise the raw <paramref name="clientId"/> (e.g.
        /// <c>"capitalism-pkce"</c>) as a fallback so unconfigured clients still show something reasonable.
        /// </summary>
        private static string ResolveAppName(string? clientId, JwtIssuerClientConfiguration? client)
        {
            if (client != null && !string.IsNullOrWhiteSpace(client.DisplayName))
            {
                return client.DisplayName;
            }

            return string.IsNullOrWhiteSpace(clientId) ? "This application" : clientId;
        }

        /// <summary>Human-friendly label for a requested OIDC/wallet scope, shown on <see cref="SelectProvider"/>.</summary>
        private static string FriendlyScopeName(string scope) => scope switch
        {
            "openid" => "Verify your identity",
            "profile" => "Your basic profile info",
            "email" => "Your email address",
            "sign" => "Sign transactions on your behalf (up to your spending limit)",
            "manage-limits" => "Change your spending limit",
            "rekey" => "⚠ Rekey your account (can permanently transfer control - risk of total fund loss)",
            _ => scope
        };

        /// <summary>
        /// Builds the consent screen's HTML, styled to match <c>SelectProviderStyles</c>. Shows a
        /// permission checklist (identity always granted; storage access and sign/manage-limits granted
        /// or denied based on the actual token/what was requested) and either a required confirm button
        /// (sensitive scopes requested, or storage access is missing) or an informational auto-continuing
        /// screen (identity-only access with storage already working).
        /// </summary>
        private static string BuildConsentHtml(
            string appName,
            bool wantsSign,
            bool wantsLimits,
            bool wantsRekey,
            bool hasStorageAccess,
            string providerDisplayName,
            string continueUrl,
            string reAuthUrl)
        {
            var encodedProviderName = WebUtility.HtmlEncode(providerDisplayName);
            var wantsPrivileges = wantsSign || wantsLimits || wantsRekey;
            var needsConfirmation = wantsPrivileges || !hasStorageAccess;
            var encodedContinueUrl = WebUtility.HtmlEncode(continueUrl);
            var encodedReAuthUrl = WebUtility.HtmlEncode(reAuthUrl);

            var permissionRows = $"""
                <div class="permission-row">
                    <span class="permission-icon granted">{CheckIconSvg}</span>
                    <div class="permission-text"><strong>Verify your identity</strong><span>Confirms who you are - always part of signing in.</span></div>
                </div>
                <div class="permission-row">
                    <span class="permission-icon {(hasStorageAccess ? "granted" : "denied")}">{(hasStorageAccess ? CheckIconSvg : CrossIconSvg)}</span>
                    <div class="permission-text"><strong>Store your account in {encodedProviderName}</strong><span>{(hasStorageAccess ? $"Granted - your encrypted Algorand key lives only in a private, app-only folder in your {encodedProviderName}." : "Not granted - your encrypted Algorand key cannot be created, read, or used without this.")}</span></div>
                </div>
                <div class="permission-row">
                    <span class="permission-icon {(wantsSign ? "granted" : "denied")}">{(wantsSign ? CheckIconSvg : CrossIconSvg)}</span>
                    <div class="permission-text"><strong>Sign transactions on your behalf</strong><span>{(wantsSign ? "Requested - lets this app sign payments/asset transfers up to your spending limit." : "Not requested - this app cannot sign or move any funds.")}</span></div>
                </div>
                <div class="permission-row">
                    <span class="permission-icon {(wantsLimits ? "granted" : "denied")}">{(wantsLimits ? CheckIconSvg : CrossIconSvg)}</span>
                    <div class="permission-text"><strong>Change your spending limit</strong><span>{(wantsLimits ? "Requested - lets this app read and update your per-transaction spending limit." : "Not requested - this app cannot change your spending limit.")}</span></div>
                </div>
                <div class="permission-row">
                    <span class="permission-icon {(wantsRekey ? "danger" : "denied")}">{(wantsRekey ? WarningIconSvg : CrossIconSvg)}</span>
                    <div class="permission-text"><strong>&#9888; Rekey your account</strong><span>{(wantsRekey ? "Requested - lets this app permanently reassign which key controls your Algorand account. If misused or leaked, this can mean TOTAL, IRREVERSIBLE LOSS of every asset in the account." : "Not requested - this app cannot reassign control of your account.")}</span></div>
                </div>
                """;

            // Shown whenever storage access is missing, regardless of what scopes the client requested -
            // explains why it's needed (an app-only folder, never the rest of the user's files) and offers
            // a manual retry beyond AuthorizeCallback's single automatic incremental-consent attempt.
            var storageSection = hasStorageAccess
                ? string.Empty
                : $"""
                    <div class="callout consent-warning">
                        <span class="callout-icon">&#128193;</span>
                        <p><strong>{encodedProviderName} storage access is missing.</strong> Biatec stores your encrypted Algorand key in a private, app-only folder in your {encodedProviderName} - this app can only ever see files it created itself, never the rest of your {encodedProviderName}. Without this access, your account can't be created, read, or used to sign anything.</p>
                    </div>
                    <a class="consent-continue-btn" href="{encodedReAuthUrl}">Grant {encodedProviderName} access</a>
                    """;

            string bodyHtml;
            if (needsConfirmation)
            {
                var privilegeSection = wantsPrivileges
                    ? $"""
                        <div class="callout consent-warning">
                            <span class="callout-icon">&#9888;&#65039;</span>
                            <p><strong>{WebUtility.HtmlEncode(appName)}</strong> is requesting account-level permissions. Review the access above and confirm to continue.</p>
                        </div>
                        """
                    : string.Empty;

                // Rekey is the single most dangerous scope Biatec issues - a separate, more alarming callout
                // than the general privilege warning above, since granting it is unlike any other scope: it
                // risks total, irreversible loss of funds if the token/session is ever compromised, not just
                // spend bounded by a configured limit.
                var rekeyDangerSection = wantsRekey
                    ? $"""
                        <div class="callout consent-danger">
                            <span class="callout-icon">&#9762;</span>
                            <p><strong>DANGER: this app is requesting the ability to REKEY your account.</strong>
                            A rekey permanently reassigns which private key controls your Algorand address. If the
                            access you grant here (or your Biatec session/token) is ever stolen or leaked, an
                            attacker could use it to take over your account forever and drain everything in it -
                            this cannot be undone by changing a password or revoking a spending limit. Only
                            continue if you fully trust <strong>{WebUtility.HtmlEncode(appName)}</strong> and
                            understand this risk.</p>
                        </div>
                        """
                    : string.Empty;

                var continueLabel = hasStorageAccess ? "Confirm &amp; Continue" : "Continue without storage access";
                var continueClass = hasStorageAccess ? "consent-continue-btn" : "consent-continue-btn secondary";

                bodyHtml = $"""
                    {storageSection}
                    {privilegeSection}
                    {rekeyDangerSection}
                    <a class="{continueClass}" href="{encodedContinueUrl}">{continueLabel}</a>
                    """;
            }
            else
            {
                // Built with plain concatenation, not raw-string interpolation - the countdown script's
                // JS braces would otherwise collide with C#'s single-brace interpolation-hole syntax.
                // Clicking anywhere on the page (including the dedicated pause button) pauses the
                // countdown - it never auto-continues out from under a user who's still reading. Clicking
                // the continue link itself still navigates normally (anchor default action, not swallowed
                // by the click listener) so "Continue" always follows continueUrl regardless of pause state.
                var autoContinueScript = "<script>" +
                    "var seconds = 5;" +
                    "var paused = false;" +
                    "var timer = null;" +
                    "var countdownEl = document.getElementById('countdown');" +
                    "var pauseBtn = document.getElementById('pauseBtn');" +
                    "function tick() {" +
                    "  seconds -= 1;" +
                    "  if (countdownEl) { countdownEl.textContent = String(Math.max(seconds, 0)); }" +
                    "  if (seconds <= 0) {" +
                    "    clearInterval(timer);" +
                    "    timer = null;" +
                    "    window.location.href = " + JsonSerializer.Serialize(continueUrl) + ";" +
                    "  }" +
                    "}" +
                    "function startTimer() { if (!timer) { timer = setInterval(tick, 1000); } }" +
                    "function stopTimer() { if (timer) { clearInterval(timer); timer = null; } }" +
                    "function setPaused(next) {" +
                    "  paused = next;" +
                    "  if (paused) { stopTimer(); } else { startTimer(); }" +
                    "  if (pauseBtn) { pauseBtn.textContent = paused ? 'Resume' : 'Pause'; }" +
                    "}" +
                    "startTimer();" +
                    "document.addEventListener('click', function (e) {" +
                    "  if (pauseBtn && e.target === pauseBtn) { setPaused(!paused); return; }" +
                    "  setPaused(true);" +
                    "});" +
                    "</script>";

                bodyHtml = $"""
                    <div class="callout consent-safe">
                        <span class="callout-icon">&#9989;</span>
                        <p>This access only verifies your identity - no transfers will be made and your spending limit will not change.</p>
                    </div>
                    <div class="consent-auto-continue-row">
                        <a class="consent-continue-btn secondary" id="continueNowBtn" href="{encodedContinueUrl}">Continue now (<span id="countdown">5</span>s)</a>
                        <button type="button" class="consent-pause-btn" id="pauseBtn">Pause</button>
                    </div>
                    {autoContinueScript}
                    """;
            }

            return $"""
                <!DOCTYPE html>
                <html lang="en">
                <head>
                    <meta charset="UTF-8">
                    <meta name="viewport" content="width=device-width, initial-scale=1.0">
                    <title>Confirm access - Biatec</title>
                    <link rel="icon" href="/logo-biatec.png">
                    <link rel="preconnect" href="https://fonts.googleapis.com">
                    <link rel="preconnect" href="https://fonts.gstatic.com" crossorigin>
                    <link href="https://fonts.googleapis.com/css2?family=Space+Grotesk:wght@400;500;600;700&family=JetBrains+Mono:wght@400;500;600&display=swap" rel="stylesheet">
                    <style>{SelectProviderStyles}</style>
                </head>
                <body>
                    <div class="aurora"></div>
                    <div class="grid-overlay"></div>
                    <div class="picker-shell consent-shell">
                        <div class="picker-card consent-card">
                            <img class="picker-logo" src="/logo-biatec.png" alt="Biatec logo">
                            <div class="eyebrow"><span class="dot"></span> Authorization request</div>
                            <h1>Confirm access for <span class="glow-text">{WebUtility.HtmlEncode(appName)}</span></h1>
                            <p class="subtitle">Here's what this app can do with your Biatec account.</p>
                            <div class="permission-list">{permissionRows}</div>
                            {bodyHtml}
                        </div>
                    </div>
                </body>
                </html>
                """;
        }

        /// <summary>
        /// OIDC token endpoint. Supports <c>grant_type=authorization_code</c> (with PKCE
        /// <c>code_verifier</c> when the code was issued with a <c>code_challenge</c>) and
        /// <c>grant_type=refresh_token</c>. Confidential clients authenticate with <c>client_id</c> +
        /// <c>client_secret</c> (form body or HTTP Basic); public clients rely on PKCE instead.
        /// </summary>
        /// <remarks>
        /// Body is <c>application/x-www-form-urlencoded</c> with fields: <c>grant_type</c>, <c>code</c>,
        /// <c>redirect_uri</c>, <c>refresh_token</c>, <c>client_id</c>, <c>client_secret</c>,
        /// <c>code_verifier</c>.
        /// </remarks>
        /// <returns>An OIDC token response (<c>access_token</c>, <c>id_token</c>, etc.) or an OAuth error.</returns>
        [AllowAnonymous]
        [HttpPost("token")]
        public async Task<IActionResult> Token()
        {
            if (!Request.HasFormContentType)
            {
                return BuildTokenError("invalid_request", "Content-Type must be application/x-www-form-urlencoded.", 400);
            }

            var form = await Request.ReadFormAsync();
            var tokenRequest = new OidcTokenRequest
            {
                GrantType = form["grant_type"].ToString(),
                Code = form["code"].ToString(),
                RedirectUri = form["redirect_uri"].ToString(),
                RefreshToken = form["refresh_token"].ToString(),
                ClientId = form["client_id"].ToString(),
                ClientSecret = form["client_secret"].ToString(),
                CodeVerifier = form["code_verifier"].ToString(),
                Resource = form["resource"].ToString()
            };

            var result = await _jwtIssuerService.ExchangeTokenAsync(tokenRequest, Request.Headers.Authorization.ToString());
            if (!result.Success)
            {
                return BuildTokenError(result.Error ?? "invalid_request", result.ErrorDescription ?? "Token request failed.", result.StatusCode);
            }

            Response.Headers.CacheControl = "no-store";
            Response.Headers.Pragma = "no-cache";
            return Ok(result.Response);
        }

        /// <summary>
        /// Returns claims for the caller's access token, sent as a <c>Bearer</c> token in the
        /// <c>Authorization</c> header.
        /// </summary>
        /// <returns>
        /// <c>sub</c>, <c>email</c>, <c>name</c>, <c>preferred_username</c>, and <c>primary_seed_address</c>
        /// (omitted if the user never granted Drive/OneDrive access). 401 if the token is
        /// missing or invalid.
        /// </returns>
        [AllowAnonymous]
        [RequiresBearerToken]
        [HttpGet("userinfo")]
        public IActionResult UserInfo()
        {
            var token = BearerTokenHelper.ExtractBearerToken(Request);
            if (string.IsNullOrWhiteSpace(token))
            {
                return Unauthorized();
            }

            var validation = _jwtIssuerService.ValidateBearerAccessToken(token);
            if (!validation.IsValid || validation.Principal == null)
            {
                return Unauthorized();
            }

            var principal = validation.Principal;
            var sub = principal.FindFirstValue(JwtRegisteredClaimNames.Sub);
            var email = principal.FindFirstValue(ClaimTypes.Email);
            var name = principal.FindFirstValue(ClaimTypes.Name);
            var primarySeedAddress = principal.FindFirstValue("primary_seed_address");

            return Ok(new
            {
                sub,
                email,
                name,
                preferred_username = principal.FindFirstValue("preferred_username"),
                primary_seed_address = primarySeedAddress
            });
        }

        /// <summary>
        /// RFC 7662 token introspection.
        /// </summary>
        /// <param name="token">The access or refresh token to check.</param>
        /// <returns>An object with at least <c>active: bool</c>, plus token metadata when active.</returns>
        [AllowAnonymous]
        [HttpPost("introspect")]
        public async Task<IActionResult> Introspect([FromForm] string token)
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                return Ok(new { active = false });
            }

            var result = await _jwtIssuerService.IntrospectAsync(token);
            return Ok(result);
        }

        /// <summary>
        /// Verifies a token, same result shape as <c>/introspect</c>. The token can be supplied either
        /// in the form body or as a <c>Bearer</c> token in the <c>Authorization</c> header.
        /// </summary>
        /// <param name="token">The token to verify (optional if supplied via the Authorization header instead).</param>
        /// <returns>An object with at least <c>active: bool</c>, plus token metadata when active.</returns>
        [AllowAnonymous]
        [RequiresBearerToken]
        [HttpPost("verify")]
        public async Task<IActionResult> Verify([FromForm] string? token)
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                token = BearerTokenHelper.ExtractBearerToken(Request);
            }

            if (string.IsNullOrWhiteSpace(token))
            {
                return BadRequest(new ProblemDetails { Detail = "Provide token in form body or Authorization Bearer header." });
            }

            var result = await _jwtIssuerService.IntrospectAsync(token);
            return Ok(result);
        }

        /// <summary>
        /// RP-Initiated Logout 1.0. Clears the Google-authenticated session cookie and, if
        /// <paramref name="postLogoutRedirectUri"/> is given, redirects there (only if it's allowlisted
        /// for the resolved client - via <paramref name="clientId"/> or the <c>aud</c> claim of
        /// <paramref name="idTokenHint"/>). Also reachable as <c>GET /logout</c>.
        /// </summary>
        /// <param name="idTokenHint">Previously issued <c>id_token</c>; its <c>aud</c> can substitute for <paramref name="clientId"/>.</param>
        /// <param name="postLogoutRedirectUri">Where to send the browser after logout; must be allowlisted for the client.</param>
        /// <param name="state">Opaque value appended to <paramref name="postLogoutRedirectUri"/> as a query parameter.</param>
        /// <param name="clientId">Registered client identifier (<c>JwtIssuer:Clients</c>).</param>
        /// <returns>A redirect to <paramref name="postLogoutRedirectUri"/> (or <c>/</c>), or a validation error.</returns>
        [AllowAnonymous]
        [HttpGet("connect/endsession")]
        [HttpGet("logout")]
        public async Task<IActionResult> EndSession(
            [FromQuery(Name = "id_token_hint")] string? idTokenHint,
            [FromQuery(Name = "post_logout_redirect_uri")] string? postLogoutRedirectUri,
            [FromQuery(Name = "state")] string? state,
            [FromQuery(Name = "client_id")] string? clientId)
        {
            clientId ??= string.IsNullOrWhiteSpace(idTokenHint) ? null : _jwtIssuerService.TryGetAudienceFromSelfIssuedToken(idTokenHint);

            JwtIssuerClientConfiguration? client = null;
            if (!string.IsNullOrWhiteSpace(clientId))
            {
                client = await _jwtIssuerService.ResolveClientAsync(clientId);
                if (client == null)
                {
                    return BadRequest(new ProblemDetails
                    {
                        Title = "invalid_client",
                        Detail = "Unknown client_id."
                    });
                }
            }

            if (!string.IsNullOrWhiteSpace(postLogoutRedirectUri))
            {
                if (client == null)
                {
                    return BadRequest(new ProblemDetails
                    {
                        Title = "invalid_request",
                        Detail = "client_id (or id_token_hint with aud) is required when post_logout_redirect_uri is provided."
                    });
                }

                if (!Uri.TryCreate(postLogoutRedirectUri, UriKind.Absolute, out _))
                {
                    return BadRequest(new ProblemDetails
                    {
                        Title = "invalid_request",
                        Detail = "post_logout_redirect_uri must be an absolute URI."
                    });
                }

                if (!IsAllowedPostLogoutRedirectUri(client, postLogoutRedirectUri))
                {
                    return BadRequest(new ProblemDetails
                    {
                        Title = "invalid_request",
                        Detail = "post_logout_redirect_uri is not allowlisted for this client_id."
                    });
                }
            }

            var redirectUri = postLogoutRedirectUri;
            if (!string.IsNullOrWhiteSpace(redirectUri) && !string.IsNullOrWhiteSpace(state))
            {
                redirectUri = QueryHelpers.AddQueryString(redirectUri, "state", state);
            }

            return SignOut(new AuthenticationProperties
            {
                RedirectUri = string.IsNullOrWhiteSpace(redirectUri) ? "/" : redirectUri
            }, CookieAuthenticationDefaults.AuthenticationScheme);
        }

        private async Task<IActionResult> FinalizeAuthorizeAsync(OidcAuthorizeRequest request, JwtIssuerClientConfiguration client)
        {
            // Resolved here (not inside JwtIssuerService) because only the controller has access to the
            // ambient cookie session at this point - FinalizeAuthorizeAsync always runs within an
            // [Authorize]-protected request (AuthorizeConsentContinue), so the caller's live Google/Microsoft
            // token (if any) is available now and won't be once the RP later calls /token server-to-server.
            // GetAmbientAccessTokenAsync (not the plain HttpContext.GetTokenAsync used elsewhere in this
            // controller) is used deliberately - Google's implementation proactively refreshes a
            // near-expired token, maximizing how long the cached copy stays valid from this moment on.
            var provider = _providerCatalog.Resolve(User.FindFirst(AuthSchemeNames.IdpClaimType)?.Value);
            var providerAccessToken = await provider.GetAmbientAccessTokenAsync();
            var providerRefreshToken = await provider.GetAmbientRefreshTokenAsync();

            var result = await _jwtIssuerService.CreateAuthorizeResponseAsync(request, client, User, providerAccessToken, providerRefreshToken);
            if (!result.Success || result.Response == null)
            {
                return BuildAuthorizeErrorResponse(request.RedirectUri, request.State, result.Error ?? "server_error", result.ErrorDescription ?? "Authorization failed.", request.ResponseMode);
            }

            await ClearAuthorizeAttemptsAsync(request);

            if (string.Equals(request.ResponseMode, "form_post", StringComparison.Ordinal))
            {
                return Content(BuildAutoPostHtml(request.RedirectUri!, result.Response), "text/html; charset=utf-8", Encoding.UTF8);
            }

            var queryValues = result.Response!.ToDictionary(kvp => kvp.Key, kvp => (string?)kvp.Value, StringComparer.Ordinal);
            var redirect = QueryHelpers.AddQueryString(request.RedirectUri!, queryValues);
            return Redirect(redirect);
        }

        private IActionResult BuildAuthorizeErrorResponse(string? redirectUri, string? state, string error, string description, string? responseMode)
        {
            if (string.IsNullOrWhiteSpace(redirectUri) || !Uri.TryCreate(redirectUri, UriKind.Absolute, out _))
            {
                return BadRequest(new ProblemDetails
                {
                    Detail = $"{error}: {description}"
                });
            }

            var payload = new Dictionary<string, string>
            {
                ["error"] = error,
                ["error_description"] = description
            };

            if (!string.IsNullOrWhiteSpace(state))
            {
                payload["state"] = state;
            }

            if (string.Equals(responseMode, "form_post", StringComparison.Ordinal))
            {
                return Content(BuildAutoPostHtml(redirectUri, payload), "text/html; charset=utf-8", Encoding.UTF8);
            }

            var queryValues = payload.ToDictionary(kvp => kvp.Key, kvp => (string?)kvp.Value, StringComparer.Ordinal);
            var url = QueryHelpers.AddQueryString(redirectUri, queryValues);
            return Redirect(url);
        }

        private IActionResult BuildTokenError(string error, string description, int statusCode)
        {
            Response.Headers.CacheControl = "no-store";
            Response.Headers.Pragma = "no-cache";
            return StatusCode(statusCode, new
            {
                error,
                error_description = description
            });
        }

        private static bool IsAllowedPostLogoutRedirectUri(JwtIssuerClientConfiguration client, string postLogoutRedirectUri)
        {
            if (!Uri.TryCreate(postLogoutRedirectUri, UriKind.Absolute, out var requested))
            {
                return false;
            }

            var allowlist = client.PostLogoutRedirectUris.Count > 0
                ? client.PostLogoutRedirectUris
                : client.RedirectUris;

            return allowlist.Any(configuredUri => RedirectUriMatcher.MatchesPostLogoutRedirect(configuredUri, requested));
        }

        private async Task<bool> TryRegisterAuthorizeAttemptAsync(OidcAuthorizeRequest request)
        {
            var cacheKey = BuildAuthorizeAttemptCacheKey(request);
            var now = DateTimeOffset.UtcNow;
            var attempts = await GetAuthorizeAttemptsAsync(cacheKey);
            var recentAttempts = attempts
                .Where(timestamp => now - timestamp < AuthorizeAttemptWindow)
                .ToList();

            if (recentAttempts.Count >= MaxAuthorizeAttempts)
            {
                return false;
            }

            recentAttempts.Add(now);

            await _cache.SetStringAsync(
                cacheKey,
                JsonSerializer.Serialize(recentAttempts),
                new DistributedCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = AuthorizeAttemptWindow
                });

            return true;
        }

        private async Task ClearAuthorizeAttemptsAsync(OidcAuthorizeRequest request)
        {
            await _cache.RemoveAsync(BuildAuthorizeAttemptCacheKey(request));
        }

        private async Task<List<DateTimeOffset>> GetAuthorizeAttemptsAsync(string cacheKey)
        {
            var json = await _cache.GetStringAsync(cacheKey);
            if (string.IsNullOrWhiteSpace(json))
            {
                return new List<DateTimeOffset>();
            }

            try
            {
                return JsonSerializer.Deserialize<List<DateTimeOffset>>(json) ?? new List<DateTimeOffset>();
            }
            catch
            {
                return new List<DateTimeOffset>();
            }
        }

        private string BuildAuthorizeAttemptCacheKey(OidcAuthorizeRequest request)
        {
            var remoteIp = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown-ip";
            var userAgent = Request.Headers.UserAgent.ToString();
            var rawKey = string.Join("|",
                request.ClientId ?? string.Empty,
                request.RedirectUri ?? string.Empty,
                remoteIp,
                userAgent);

            var encodedKey = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(rawKey));
            return AuthorizeAttemptPrefix + encodedKey;
        }

        private static string BuildAutoPostHtml(string actionUrl, Dictionary<string, string> values)
        {
            var sb = new StringBuilder();
            sb.Append("<!DOCTYPE html><html><head><meta charset=\"utf-8\"></head><body>");
            sb.Append($"<form id=\"oidcform\" method=\"post\" action=\"{WebUtility.HtmlEncode(actionUrl)}\">");

            foreach (var pair in values)
            {
                sb.Append($"<input type=\"hidden\" name=\"{WebUtility.HtmlEncode(pair.Key)}\" value=\"{WebUtility.HtmlEncode(pair.Value)}\" />");
            }

            sb.Append("</form><script>document.getElementById('oidcform').submit();</script></body></html>");
            return sb.ToString();
        }
    }
}
