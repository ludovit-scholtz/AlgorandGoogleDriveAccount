using AlgorandGoogleDriveAccount.BusinessLogic;
using AlgorandGoogleDriveAccount.Model;
using Google.Apis.Auth.AspNetCore3;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using System.Text;

namespace AlgorandGoogleDriveAccount.Controllers
{
    [ApiController]
    [Route("")]
    public class JwtIssuerController : ControllerBase
    {
        private readonly IJwtIssuerService _jwtIssuerService;

        public JwtIssuerController(IJwtIssuerService jwtIssuerService)
        {
            _jwtIssuerService = jwtIssuerService;
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
            [FromQuery(Name = "nonce")] string? nonce)
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
                Nonce = nonce
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
                ClientSecret = form["client_secret"].ToString()
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

        private async Task<IActionResult> FinalizeAuthorizeAsync(OidcAuthorizeRequest request, JwtIssuerClientConfiguration client)
        {
            var result = await _jwtIssuerService.CreateAuthorizeResponseAsync(request, client, User);
            if (!result.Success || result.Response == null)
            {
                return BuildAuthorizeErrorResponse(request.RedirectUri, request.State, result.Error ?? "server_error", result.ErrorDescription ?? "Authorization failed.", request.ResponseMode);
            }

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
