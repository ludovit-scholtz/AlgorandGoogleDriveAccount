using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using BiatecOIDC.BusinessLogic;
using BiatecOIDC.Helper;
using BiatecOIDC.Model;
using BiatecSelfCustodyCore.BusinessLogic;
using BiatecSelfCustodyCore.Model;
using BiatecSelfCustodyCore.Providers;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Caching.Distributed;

namespace BiatecOIDC.Controllers
{
    /// <summary>
    /// OpenID Connect identity provider: discovery/JWKS, the authorize/token endpoints (standard
    /// <c>response_type=code</c> with optional PKCE, plus a legacy <c>returnUrl</c> direct <c>id_token</c>
    /// flow), userinfo, token introspection/verification, and RP-Initiated Logout. Issues RS256-signed
    /// tokens carrying Algorand-identity claims (<c>algorand_address</c>) for whitelisted third-party
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
                CodeChallengeMethod = codeChallengeMethod
            };

            var validation = await _jwtIssuerService.ValidateAuthorizeRequestAsync(authRequest);
            if (!validation.IsValid || validation.NormalizedRequest == null || validation.Client == null)
            {
                return BuildAuthorizeErrorResponse(authRequest.RedirectUri ?? authRequest.ReturnUrl, authRequest.State, validation.Error ?? "invalid_request", validation.ErrorDescription ?? "Invalid request.", authRequest.ResponseMode);
            }

            var normalizedRequest = validation.NormalizedRequest;
            var client = validation.Client;

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

            return await FinalizeAuthorizeAsync(normalizedRequest, client);
        }

        /// <summary>
        /// Provider picker shown when <c>/authorize</c> is called with no <c>idp</c> fast-track parameter
        /// and the caller isn't already signed in. Not part of the public OIDC contract.
        /// </summary>
        /// <param name="requestId">Opaque id of the pending authorize request stored by <c>/authorize</c>.</param>
        [AllowAnonymous]
        [HttpGet("select-provider")]
        public IActionResult SelectProvider([FromQuery] string requestId)
        {
            var sb = new StringBuilder();
            sb.Append("<!DOCTYPE html><html><head><meta charset=\"utf-8\"><title>Sign in to Biatec</title></head>");
            sb.Append("<body style=\"font-family:sans-serif;max-width:420px;margin:4rem auto;text-align:center;\">");
            sb.Append("<h1>Sign in to Biatec</h1>");
            sb.Append("<p>Choose how you'd like to sign in. Your self-custody account is stored in the matching cloud storage.</p>");

            // One button per registered ICloudStorageProvider - adding a new provider needs no
            // change here, it just shows up.
            foreach (var provider in _providerCatalog.All)
            {
                var url = Url.Action(nameof(AuthorizeChallenge), "JwtIssuer", new { requestId, idp = provider.Name }, Request.Scheme);
                sb.Append($"<p><a href=\"{WebUtility.HtmlEncode(url)}\" style=\"display:block;margin:1rem 0;padding:0.75rem;background:#333;color:#fff;text-decoration:none;border-radius:6px;\">Continue with {WebUtility.HtmlEncode(provider.DisplayName)}</a></p>");
            }

            sb.Append("</body></html>");

            return Content(sb.ToString(), "text/html; charset=utf-8", Encoding.UTF8);
        }

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

            return await FinalizeAuthorizeAsync(validation.NormalizedRequest, validation.Client);
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
                CodeVerifier = form["code_verifier"].ToString()
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
        /// <c>sub</c>, <c>email</c>, <c>name</c>, <c>preferred_username</c>, and <c>algorand_address</c>
        /// (omitted if the user never granted Drive access). 401 if the token is missing or invalid.
        /// </returns>
        [AllowAnonymous]
        [HttpGet("userinfo")]
        public IActionResult UserInfo()
        {
            var token = ExtractBearerToken();
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
            var algorandAddress = principal.FindFirstValue("algorand_address");

            return Ok(new
            {
                sub,
                email,
                name,
                preferred_username = principal.FindFirstValue("preferred_username"),
                algorand_address = algorandAddress
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
        [HttpPost("verify")]
        public async Task<IActionResult> Verify([FromForm] string? token)
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                token = ExtractBearerToken();
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
        public IActionResult EndSession(
            [FromQuery(Name = "id_token_hint")] string? idTokenHint,
            [FromQuery(Name = "post_logout_redirect_uri")] string? postLogoutRedirectUri,
            [FromQuery(Name = "state")] string? state,
            [FromQuery(Name = "client_id")] string? clientId)
        {
            clientId ??= string.IsNullOrWhiteSpace(idTokenHint) ? null : _jwtIssuerService.TryGetAudienceFromSelfIssuedToken(idTokenHint);
            var issuerConfig = HttpContext.RequestServices.GetRequiredService<IConfiguration>().GetSection("JwtIssuer").Get<JwtIssuerConfiguration>()
                ?? new JwtIssuerConfiguration();

            JwtIssuerClientConfiguration? client = null;
            if (!string.IsNullOrWhiteSpace(clientId))
            {
                client = issuerConfig.Clients.FirstOrDefault(c => string.Equals(c.ClientId, clientId, StringComparison.Ordinal));
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
            var result = await _jwtIssuerService.CreateAuthorizeResponseAsync(request, client, User);
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

        private string? ExtractBearerToken()
        {
            var header = Request.Headers.Authorization.ToString();
            const string prefix = "Bearer ";
            if (string.IsNullOrWhiteSpace(header) || !header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return header[prefix.Length..].Trim();
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
