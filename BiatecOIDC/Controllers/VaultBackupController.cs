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

        /// <summary>
        /// Browser entry point - redirects to the target provider's own consent screen. Not an API call.
        /// Requires the browser to already be signed in to Biatec (the ambient cookie session) as the exact
        /// account the pending backup belongs to - see <see cref="EnsureBrowserOwnsBackup"/>.
        /// </summary>
        [AllowAnonymous]
        [HttpGet("authorize")]
        public async Task<IActionResult> Authorize([FromQuery] string linkId)
        {
            var pending = await _backupService.GetPendingAsync(linkId);
            if (pending == null)
            {
                return BadRequest(new ProblemDetails { Title = "invalid_link", Detail = "This backup link has expired or was never started." });
            }

            var ownershipError = EnsureBrowserOwnsBackup(pending);
            if (ownershipError != null)
            {
                return ownershipError;
            }

            var provider = _providerCatalog.Resolve(pending.TargetProvider);
            var callbackUrl = Url.Action(nameof(Callback), "VaultBackup", null, Request.Scheme)!;
            return Redirect(provider.BuildAuthorizationUrl(callbackUrl, linkId));
        }

        /// <summary>
        /// Browser callback after the target provider's consent screen. Renders a plain confirmation page - not
        /// an API call. Re-checks <see cref="EnsureBrowserOwnsBackup"/> (not just <see cref="Authorize"/>) as
        /// defense in depth, in case the ambient session changed between the two browser round trips.
        /// </summary>
        [AllowAnonymous]
        [HttpGet("callback")]
        public async Task<IActionResult> Callback([FromQuery] string code, [FromQuery] string state)
        {
            var pending = await _backupService.GetPendingAsync(state);
            if (pending == null)
            {
                return Content(BuildResultHtml(false, "This backup link has expired or was already used. Please start again."), "text/html; charset=utf-8", Encoding.UTF8);
            }

            var ownershipError = EnsureBrowserOwnsBackup(pending);
            if (ownershipError != null)
            {
                return ownershipError;
            }

            var callbackUrl = Url.Action(nameof(Callback), "VaultBackup", null, Request.Scheme)!;
            var (success, error) = await _backupService.HandleCallbackAsync(state, code, callbackUrl);
            return Content(BuildResultHtml(success, error), "text/html; charset=utf-8", Encoding.UTF8);
        }

        /// <summary>
        /// Confirms the browser completing this step of the vault-backup OAuth round trip is already signed in
        /// to Biatec (the ambient cookie session populated by the normal Google/Microsoft sign-in used
        /// elsewhere in this app) as the exact account <paramref name="pending"/> belongs to.
        /// </summary>
        /// <remarks>
        /// Without this check, an attacker who starts a backup for their own account (<see cref="Start"/> only
        /// requires their own valid bearer token - no relationship to any victim is needed) could send an
        /// unrelated victim a link straight to <see cref="Authorize"/>; the victim's own, entirely genuine
        /// consent on the target provider would then get captured under the attacker's pending backup, letting
        /// the attacker complete the backup (<see cref="Complete"/>) using the victim's captured token to write
        /// the attacker's own vault ciphertext into the victim's cloud storage, under the victim's own account
        /// file name (security audit finding H-01/R-020). A same-browser anti-CSRF cookie alone would not close
        /// this gap - the victim's browser genuinely is the one completing both <see cref="Authorize"/> and
        /// <see cref="Callback"/>, so what must be checked is not "same browser" but "the right account". A
        /// victim who has never signed in to Biatec as the attacker's email - which is the case for every victim
        /// in this attack, since the whole point is that they are unrelated to the attacker's account - has no
        /// ambient session matching <paramref name="pending"/>.Email and is refused here, before the flow ever
        /// reaches the target provider's consent screen.
        /// </remarks>
        private IActionResult? EnsureBrowserOwnsBackup(PendingVaultBackup pending)
        {
            var sessionEmail = User.FindFirstValue(ClaimTypes.Email);
            if (User.Identity?.IsAuthenticated != true || !string.Equals(sessionEmail, pending.Email, StringComparison.OrdinalIgnoreCase))
            {
                return Content(
                    BuildResultHtml(false, "You must be signed in to Biatec as the account that started this backup to continue. Please sign in to Biatec in this browser and try again."),
                    "text/html; charset=utf-8",
                    Encoding.UTF8);
            }

            return null;
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
