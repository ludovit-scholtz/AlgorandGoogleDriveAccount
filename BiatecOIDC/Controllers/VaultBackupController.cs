using System.Net;
using System.Security.Claims;
using System.Text;
using BiatecOIDC.BusinessLogic;
using BiatecOIDC.Helper;
using BiatecOIDC.Model;
using BiatecOIDC.Swagger;
using BiatecSelfCustodyCore.Model;
using BiatecSelfCustodyCore.Providers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BiatecOIDC.Controllers
{
    /// <summary>
    /// Explicit, user-triggered backup of the caller's encrypted seed vault to a second cloud provider - see
    /// <see cref="IVaultBackupService"/>'s remarks for the full design (why this doesn't use the normal
    /// <c>Challenge()</c>/cookie sign-in flow). A three-step flow: <see cref="Start"/> (bearer API call) to
    /// get a link, <see cref="Authorize"/>/<see cref="Callback"/> (a browser round trip the user completes),
    /// then <see cref="Complete"/> (bearer API call) to actually perform the copy. Nothing here ever changes
    /// the caller's primary <c>biatec_idp</c> - the second provider's token is used exactly once and never
    /// cached.
    /// </summary>
    [ApiController]
    [Route("wallet/backup")]
    public class VaultBackupController : ControllerBase
    {
        private readonly IJwtIssuerService _jwtIssuerService;
        private readonly IVaultBackupService _backupService;
        private readonly ICloudStorageProviderCatalog _providerCatalog;
        private readonly IProviderAccessTokenProtector _providerTokenProtector;
        private readonly ILogger<VaultBackupController> _logger;

        public VaultBackupController(
            IJwtIssuerService jwtIssuerService,
            IVaultBackupService backupService,
            ICloudStorageProviderCatalog providerCatalog,
            IProviderAccessTokenProtector providerTokenProtector,
            ILogger<VaultBackupController> logger)
        {
            _jwtIssuerService = jwtIssuerService;
            _backupService = backupService;
            _providerCatalog = providerCatalog;
            _providerTokenProtector = providerTokenProtector;
            _logger = logger;
        }

        /// <summary>Begins a backup link to <see cref="StartVaultBackupRequest.TargetProvider"/>. Returns a URL for the user to open in a browser.</summary>
        /// <response code="200">The link id and browser URL to continue with.</response>
        /// <response code="400">The target provider is the same as the caller's primary one, or isn't recognized/configured.</response>
        /// <response code="401">The bearer token is missing, invalid, or expired.</response>
        /// <response code="403">The token lacks the <c>sign</c> claim.</response>
        [AllowAnonymous]
        [RequiresBearerToken]
        [HttpPost("start")]
        public async Task<IActionResult> Start([FromBody] StartVaultBackupRequest request)
        {
            var authError = TryAuthenticate("sign", out var principal);
            if (authError != null)
            {
                return authError;
            }

            var email = principal!.FindFirstValue(ClaimTypes.Email)!;
            var primaryProvider = principal.FindFirstValue(AuthSchemeNames.IdpClaimType) ?? string.Empty;

            try
            {
                var linkId = await _backupService.StartAsync(email, primaryProvider, request.TargetProvider);
                var authorizeUrl = Url.Action(nameof(Authorize), "VaultBackup", new { linkId }, Request.Scheme)!;
                return Ok(new StartVaultBackupResponse { LinkId = linkId, AuthorizeUrl = authorizeUrl });
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new ProblemDetails { Title = "invalid_target_provider", Detail = ex.Message });
            }
        }

        /// <summary>Browser entry point - redirects to the target provider's own consent screen. Not an API call.</summary>
        [AllowAnonymous]
        [HttpGet("authorize")]
        public async Task<IActionResult> Authorize([FromQuery] string linkId)
        {
            var pending = await _backupService.GetPendingAsync(linkId);
            if (pending == null)
            {
                return BadRequest(new ProblemDetails { Title = "invalid_link", Detail = "This backup link has expired or was never started." });
            }

            var provider = _providerCatalog.Resolve(pending.TargetProvider);
            var callbackUrl = Url.Action(nameof(Callback), "VaultBackup", null, Request.Scheme)!;
            return Redirect(provider.BuildAuthorizationUrl(callbackUrl, linkId));
        }

        /// <summary>Browser callback after the target provider's consent screen. Renders a plain confirmation page - not an API call.</summary>
        [AllowAnonymous]
        [HttpGet("callback")]
        public async Task<IActionResult> Callback([FromQuery] string code, [FromQuery] string state)
        {
            var callbackUrl = Url.Action(nameof(Callback), "VaultBackup", null, Request.Scheme)!;
            var (success, error) = await _backupService.HandleCallbackAsync(state, code, callbackUrl);
            return Content(BuildResultHtml(success, error), "text/html; charset=utf-8", Encoding.UTF8);
        }

        /// <summary>Finishes the backup: copies the vault from the caller's primary provider to the newly-linked target provider.</summary>
        /// <response code="200">The backup completed.</response>
        /// <response code="400">The link is missing/expired/already used, or the copy itself failed.</response>
        /// <response code="401">The bearer token is missing, invalid, or expired, or no cached provider access token is available.</response>
        /// <response code="403">The token lacks the <c>sign</c> claim.</response>
        [AllowAnonymous]
        [RequiresBearerToken]
        [HttpPost("complete")]
        public async Task<IActionResult> Complete([FromBody] CompleteVaultBackupRequest request)
        {
            var authError = TryAuthenticate("sign", out var principal);
            if (authError != null)
            {
                return authError;
            }

            var email = principal!.FindFirstValue(ClaimTypes.Email)!;
            var primaryProvider = principal.FindFirstValue(AuthSchemeNames.IdpClaimType) ?? string.Empty;
            var primaryAccessToken = ResolveProviderAccessToken(principal, email);

            var (success, error) = await _backupService.CompleteAsync(email, primaryProvider, primaryAccessToken, request.LinkId);
            if (!success)
            {
                return BadRequest(new ProblemDetails { Title = "backup_failed", Detail = error });
            }

            return Ok();
        }

        private static string BuildResultHtml(bool success, string? error)
        {
            var title = success ? "Backup authorized" : "Backup failed";
            var message = success
                ? "This provider is now authorized. Return to the app to finish the backup."
                : WebUtility.HtmlEncode(error ?? "Something went wrong.");
            return $"""
                <!DOCTYPE html>
                <html lang="en">
                <head><meta charset="UTF-8"><title>{title} - Biatec</title></head>
                <body style="font-family: sans-serif; text-align: center; padding: 3rem;">
                    <h1>{title}</h1>
                    <p>{message}</p>
                </body>
                </html>
                """;
        }

        /// <summary>
        /// The provider access token to use for this call, decrypted from the bearer token's own
        /// <c>provider_token</c> claim - same mechanism as <see cref="WalletController"/>.
        /// </summary>
        private string? ResolveProviderAccessToken(ClaimsPrincipal principal, string email)
        {
            var cachedProtectedToken = principal.FindFirstValue(ProviderAccessTokenProtector.ClaimType);
            return _providerTokenProtector.Unprotect(cachedProtectedToken, email);
        }

        /// <summary>Same manual bearer-token pattern as <see cref="WalletController"/> - see its remarks for why.</summary>
        private IActionResult? TryAuthenticate(string? requiredClaim, out ClaimsPrincipal? principal)
        {
            principal = null;

            var token = BearerTokenHelper.ExtractBearerToken(Request);
            if (string.IsNullOrWhiteSpace(token))
            {
                return Unauthorized(new ProblemDetails { Title = "invalid_token", Detail = "Missing bearer token." });
            }

            var validation = _jwtIssuerService.ValidateBearerAccessToken(token);
            if (!validation.IsValid || validation.Principal == null)
            {
                return Unauthorized(new ProblemDetails { Title = "invalid_token", Detail = validation.Error ?? "Invalid or expired token." });
            }

            var email = validation.Principal.FindFirstValue(ClaimTypes.Email);
            if (string.IsNullOrWhiteSpace(email))
            {
                return Unauthorized(new ProblemDetails { Title = "invalid_token", Detail = "Token has no email claim." });
            }

            if (requiredClaim != null && !string.Equals(validation.Principal.FindFirstValue(requiredClaim), "true", StringComparison.Ordinal))
            {
                return StatusCode(StatusCodes.Status403Forbidden, new ProblemDetails
                {
                    Title = "insufficient_scope",
                    Detail = $"The access token does not have the required '{requiredClaim}' scope/claim."
                });
            }

            principal = validation.Principal;
            return null;
        }
    }
}
