using AlgorandGoogleDriveAccount.BusinessLogic;
using Google.Apis.Auth.AspNetCore3;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace AlgorandGoogleDriveAccount.Controllers
{
    /// <summary>
    /// Self-custody operations against the signed-in user's Algorand account: signing, address lookup,
    /// and the Google session used to unlock the AES-encrypted key stored in the user's own Drive.
    /// </summary>
    [Authorize]
    [ApiController]
    [Route("api/drive")]
    public class DriveController : ControllerBase
    {
        private readonly IDriveService _driveService;
        private readonly ILogger<DriveController> _logger;

        public DriveController(
            IDriveService driveService,
            ILogger<DriveController> logger)
        {
            _driveService = driveService;
            _logger = logger;
        }

        /// <summary>
        /// Signs an unsigned transaction, or combines a signed multisig transaction with the
        /// account's private key, using the key decrypted from the user's own Google Drive.
        /// </summary>
        /// <param name="txMsgPack">The msgpack-encoded Algorand transaction to sign.</param>
        /// <returns>The signed transaction, msgpack-encoded.</returns>
        [Authorize]
        [HttpPost("sign")]
        public async Task<ActionResult<byte[]>> Sign([FromForm] byte[] txMsgPack)
        {
            try
            {
                var email = User.FindFirst(ClaimTypes.Email)?.Value;
                if (string.IsNullOrEmpty(email))
                {
                    return BadRequest(new ProblemDetails() { Detail = "Email not found in claims. Please login first." });
                }

                var signedTransaction = await _driveService.SignTransactionAsync(email, txMsgPack);
                return Ok(signedTransaction);
            }
            catch (ArgumentException exc)
            {
                return BadRequest(new ProblemDetails() { Detail = exc.Message });
            }
            catch (Exception exc)
            {
                _logger?.LogError(exc, "Error signing transaction");
                return BadRequest(new ProblemDetails() { Detail = "Unable to sign the transaction." });
            }
        }

        /// <summary>
        /// Get the current user's access token for use with MCP server
        /// </summary>
        /// <returns>Access token for Google Drive API</returns>
        [Authorize]
        [HttpGet("access-token")]
        public async Task<ActionResult<string>> GetAccessToken()
        {
            try
            {
                var accessToken = await HttpContext.GetTokenAsync("access_token");
                if (string.IsNullOrEmpty(accessToken))
                {
                    return BadRequest(new ProblemDetails() { Detail = "No access token found. Please login first." });
                }
                
                return Ok(accessToken);
            }
            catch (Exception exc)
            {
                _logger?.LogError(exc, "Error retrieving access token");
                return BadRequest(new ProblemDetails() { Detail = "Unable to retrieve the access token." });
            }
        }

        /// <summary>
        /// Get the account address for the authenticated user
        /// </summary>
        /// <returns>The user's Algorand account address.</returns>
        [Authorize]
        [HttpGet("address")]
        public async Task<ActionResult<string>> GetAddress()
        {
            try
            {
                var email = User.FindFirst(ClaimTypes.Email)?.Value;
                if (string.IsNullOrEmpty(email))
                {
                    return BadRequest(new ProblemDetails() { Detail = "Email not found in claims. Please login first." });
                }

                var address = await _driveService.GetAccountAddressAsync(email);
                return Ok(address);
            }
            catch (ArgumentException exc)
            {
                return BadRequest(new ProblemDetails() { Detail = exc.Message });
            }
            catch (Exception exc)
            {
                _logger?.LogError(exc, "Error retrieving account address");
                return BadRequest(new ProblemDetails() { Detail = "Unable to retrieve the account address." });
            }
        }

        /// <summary>
        /// Starts the Google sign-in flow (e.g. for the "Login with Google" link in Swagger UI) and
        /// redirects back to <paramref name="redirectUri"/> once the session cookie is set.
        /// </summary>
        /// <param name="redirectUri">
        /// Where to send the browser after sign-in. Must be a local path (validated with
        /// <c>Url.IsLocalUrl</c>) - defaults to <c>/swagger/</c> if omitted or not local.
        /// </param>
        [AllowAnonymous]
        [HttpGet("login")]
        public IActionResult Login(string? redirectUri = null)
        {
            return Challenge(new AuthenticationProperties
            {
                RedirectUri = ResolveLocalRedirectUri(redirectUri)
            }, GoogleOpenIdConnectDefaults.AuthenticationScheme);
        }

        /// <summary>
        /// Clears the Google-authenticated session cookie and redirects back to
        /// <paramref name="redirectUri"/>.
        /// </summary>
        /// <param name="redirectUri">
        /// Where to send the browser after sign-out. Must be a local path (validated with
        /// <c>Url.IsLocalUrl</c>) - defaults to <c>/swagger/</c> if omitted or not local.
        /// </param>
        [Authorize]
        [HttpGet("logout")]
        public IActionResult Logout(string? redirectUri = null)
        {
            return SignOut(new AuthenticationProperties
            {
                RedirectUri = ResolveLocalRedirectUri(redirectUri)
            }, CookieAuthenticationDefaults.AuthenticationScheme);
        }

        /// <summary>
        /// Only ever redirects within this app. <paramref name="redirectUri"/> is caller-supplied
        /// (a query string parameter), so it must be validated as local to avoid an open redirect.
        /// </summary>
        private string ResolveLocalRedirectUri(string? redirectUri)
        {
            if (!string.IsNullOrEmpty(redirectUri) && Url.IsLocalUrl(redirectUri))
            {
                return redirectUri;
            }

            return Url.Content("~/swagger/");
        }
    }
}