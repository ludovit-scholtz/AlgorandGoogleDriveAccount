using System.Security.Claims;
using BiatecOIDC.BusinessLogic;
using BiatecOIDC.Helper;
using BiatecOIDC.Model;
using BiatecOIDC.Swagger;
using BiatecSelfCustodyCore.Model;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BiatecOIDC.Controllers
{
    /// <summary>
    /// Self-custody wallet API: signs Algorand transaction groups and manages the caller's spending
    /// limit. Every endpoint here requires a Biatec OIDC access token (<c>Authorization: Bearer</c>).
    /// State-changing endpoints additionally require the relevant scope-derived claim - <c>sign</c> for
    /// <see cref="SignTransactionGroup"/>, <c>manage-limits</c> for <see cref="UpdateSpendingLimit"/> -
    /// exactly as granted at <c>/authorize</c> (see <c>JwtIssuerService.CreateAccessToken</c>); a token
    /// missing the claim is rejected with 403, never silently treated as authorized.
    /// <see cref="GetSpendingLimit"/> is read-only and only requires a validly-authenticated caller (the
    /// standard <c>openid</c> scope) to verify the caller's identity - no <c>manage-limits</c> claim needed.
    /// </summary>
    [ApiController]
    [Route("wallet")]
    public class WalletController : ControllerBase
    {
        private readonly IJwtIssuerService _jwtIssuerService;
        private readonly IWalletService _walletService;
        private readonly ISpendingLimitService _spendingLimitService;
        private readonly ILogger<WalletController> _logger;

        public WalletController(
            IJwtIssuerService jwtIssuerService,
            IWalletService walletService,
            ISpendingLimitService spendingLimitService,
            ILogger<WalletController> logger)
        {
            _jwtIssuerService = jwtIssuerService;
            _walletService = walletService;
            _spendingLimitService = spendingLimitService;
            _logger = logger;
        }

        /// <summary>
        /// Signs an Algorand transaction group. Every payment/asset-transfer in the group is checked
        /// against the caller's spending limit before anything is signed.
        /// </summary>
        /// <param name="request">The transactions to sign (base64 msgpack) plus the provider access token needed to read the self-custody file.</param>
        /// <returns>The signed transactions (base64 msgpack), in the same order as the request.</returns>
        /// <response code="200">All transactions were within limit and signed successfully.</response>
        /// <response code="400">The request was malformed, or a transaction could not be decoded.</response>
        /// <response code="401">The bearer token is missing, invalid, or expired.</response>
        /// <response code="403">The token lacks the <c>sign</c> claim, or a transaction exceeds the caller's spending limit.</response>
        [AllowAnonymous]
        [RequiresBearerToken]
        [HttpPost("sign")]
        public async Task<IActionResult> SignTransactionGroup([FromBody] SignTransactionGroupRequest request)
        {
            var authError = TryAuthenticate(WalletScopes.Sign, out var principal);
            if (authError != null)
            {
                return authError;
            }

            if (request.Transactions == null || request.Transactions.Count == 0)
            {
                return BadRequest(new ProblemDetails { Title = "invalid_request", Detail = "At least one transaction is required." });
            }

            List<byte[]> decodedTransactions;
            try
            {
                decodedTransactions = request.Transactions.Select(Convert.FromBase64String).ToList();
            }
            catch (FormatException)
            {
                return BadRequest(new ProblemDetails { Title = "invalid_request", Detail = "Each transaction must be base64-encoded." });
            }

            var email = principal!.FindFirstValue(ClaimTypes.Email)!;
            var provider = principal.FindFirstValue(AuthSchemeNames.IdpClaimType) ?? string.Empty;

            try
            {
                var signed = await _walletService.SignTransactionGroupAsync(email, provider, decodedTransactions, request.AccessToken);
                return Ok(new SignTransactionGroupResponse
                {
                    SignedTransactions = signed.Select(Convert.ToBase64String).ToList()
                });
            }
            catch (SpendingLimitExceededException ex)
            {
                return StatusCode(StatusCodes.Status403Forbidden, new ProblemDetails { Title = "spending_limit_exceeded", Detail = ex.Message });
            }
            catch (FormatException ex)
            {
                return BadRequest(new ProblemDetails { Title = "invalid_request", Detail = ex.Message });
            }
            catch (UnauthorizedAccessException ex)
            {
                return StatusCode(StatusCodes.Status401Unauthorized, new ProblemDetails { Title = "storage_access_denied", Detail = ex.Message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error signing transaction group for {Email}.", email);
                return StatusCode(StatusCodes.Status500InternalServerError, new ProblemDetails { Title = "server_error", Detail = "Unable to sign the transaction group." });
            }
        }

        /// <summary>Returns the caller's current per-transaction spending limit.</summary>
        /// <response code="200">The current limit (<c>0</c> means unbounded).</response>
        /// <response code="401">The bearer token is missing, invalid, or expired.</response>
        [AllowAnonymous]
        [RequiresBearerToken]
        [HttpGet("limits")]
        public async Task<IActionResult> GetSpendingLimit()
        {
            var authError = TryAuthenticate(requiredClaim: null, out var principal);
            if (authError != null)
            {
                return authError;
            }

            var email = principal!.FindFirstValue(ClaimTypes.Email)!;
            var maxAmount = await _spendingLimitService.GetMaxAmountPerTransactionAsync(email);
            return Ok(new SpendingLimitResponse { MaxAmountPerTransaction = maxAmount });
        }

        /// <summary>Sets the caller's per-transaction spending limit.</summary>
        /// <param name="request">The new limit (<c>0</c> to clear/unbound it).</param>
        /// <response code="200">The limit was updated.</response>
        /// <response code="401">The bearer token is missing, invalid, or expired.</response>
        /// <response code="403">The token lacks the <c>manage-limits</c> claim.</response>
        [AllowAnonymous]
        [RequiresBearerToken]
        [HttpPut("limits")]
        public async Task<IActionResult> UpdateSpendingLimit([FromBody] UpdateSpendingLimitRequest request)
        {
            var authError = TryAuthenticate(WalletScopes.ManageLimits, out var principal);
            if (authError != null)
            {
                return authError;
            }

            var email = principal!.FindFirstValue(ClaimTypes.Email)!;
            await _spendingLimitService.SetMaxAmountPerTransactionAsync(email, request.MaxAmountPerTransaction);
            _logger.LogInformation("{Email} updated their spending limit to {MaxAmount}.", email, request.MaxAmountPerTransaction);
            return Ok(new SpendingLimitResponse { MaxAmountPerTransaction = request.MaxAmountPerTransaction });
        }

        /// <summary>
        /// Extracts and validates the bearer access token and confirms it carries an <c>email</c> claim
        /// identifying the wallet owner. When <paramref name="requiredClaim"/> is non-null, also confirms
        /// the token carries that claim (stamped from the matching OIDC scope - see
        /// <c>JwtIssuerService.CreateAccessToken</c>); when <c>null</c>, any validly-authenticated caller
        /// (the standard <c>openid</c> scope) is sufficient - used for read-only identity-verification
        /// endpoints that don't need a scope beyond proving who the caller is. Returns <c>null</c> and
        /// sets <paramref name="principal"/> on success; otherwise returns the error response to return
        /// directly, and <paramref name="principal"/> is <c>null</c>.
        /// </summary>
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

        private static class WalletScopes
        {
            public const string Sign = "sign";
            public const string ManageLimits = "manage-limits";
        }
    }
}
