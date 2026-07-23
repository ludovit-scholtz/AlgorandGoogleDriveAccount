using AlgorandGoogleDriveAccount.BusinessLogic;
using AlgorandGoogleDriveAccount.Helper;
using AlgorandGoogleDriveAccount.Model;
using Google.Apis.Auth.AspNetCore3;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Caching.Distributed;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;

namespace AlgorandGoogleDriveAccount.Controllers
{
    [ApiController]
    [Route("")]
    public class JwtIssuerController : ControllerBase
    {
        private const string AuthorizeAttemptPrefix = "oidc:authorize-attempts:";
        private const int MaxAuthorizeAttempts = 3;
        private static readonly TimeSpan AuthorizeAttemptWindow = TimeSpan.FromSeconds(10);

        private readonly IJwtIssuerService _jwtIssuerService;
        private readonly IDistributedCache _cache;

        public JwtIssuerController(IJwtIssuerService jwtIssuerService, IDistributedCache cache)
        {
            _jwtIssuerService = jwtIssuerService;
            _cache = cache;
        }

        [AllowAnonymous]
        [HttpGet(".well-known/openid-configuration")]
        public IActionResult OpenIdConfiguration()
        {
            return Ok(_jwtIssuerService.GetDiscoveryDocument(Request));
        }

        [AllowAnonymous]
        [HttpGet(".well-known/jwks.json")]
        public IActionResult Jwks()
        {
            return Ok(_jwtIssuerService.GetJsonWebKeySet());
        }

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
            [FromQuery(Name = "code_challenge_method")] string? codeChallengeMethod)
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
                var callbackUrl = Url.Action(nameof(AuthorizeCallback), "JwtIssuer", new { requestId }, Request.Scheme);

                var properties = new AuthenticationProperties
                {
                    RedirectUri = callbackUrl
                };

                return Challenge(properties, GoogleOpenIdConnectDefaults.AuthenticationScheme);
            }

            return await FinalizeAuthorizeAsync(normalizedRequest, client);
        }

        [Authorize]
        [HttpGet("authorize/callback")]
        public async Task<IActionResult> AuthorizeCallback([FromQuery] string requestId)
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

            return await FinalizeAuthorizeAsync(validation.NormalizedRequest, validation.Client);
        }

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

        [AllowAnonymous]
        [HttpGet("connect/endsession")]
        [HttpGet("logout")]
        public IActionResult EndSession(
            [FromQuery(Name = "id_token_hint")] string? idTokenHint,
            [FromQuery(Name = "post_logout_redirect_uri")] string? postLogoutRedirectUri,
            [FromQuery(Name = "state")] string? state,
            [FromQuery(Name = "client_id")] string? clientId)
        {
            clientId ??= TryGetClientIdFromIdTokenHint(idTokenHint);
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

        private static string? TryGetClientIdFromIdTokenHint(string? idTokenHint)
        {
            if (string.IsNullOrWhiteSpace(idTokenHint))
            {
                return null;
            }

            var tokenHandler = new JwtSecurityTokenHandler();
            if (!tokenHandler.CanReadToken(idTokenHint))
            {
                return null;
            }

            try
            {
                var token = tokenHandler.ReadJwtToken(idTokenHint);
                return token.Audiences.FirstOrDefault();
            }
            catch
            {
                return null;
            }
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
