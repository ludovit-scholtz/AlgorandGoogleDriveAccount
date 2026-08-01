using System.Security.Claims;
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
    /// Self-custody wallet API: signs Algorand transaction groups and manages the caller's
    /// daily/weekly/monthly spending limits. Every endpoint here requires a Biatec OIDC access token
    /// (<c>Authorization: Bearer</c>). State-changing endpoints additionally require the relevant
    /// scope-derived claim - <c>sign</c> for <see cref="SignTransactionGroup"/>, <c>manage-limits</c> for
    /// <see cref="UpdateSpendingLimit"/> - exactly as granted at <c>/authorize</c> (see
    /// <c>JwtIssuerService.CreateAccessToken</c>); a token missing the claim is rejected with 403, never
    /// silently treated as authorized. <see cref="GetSpendingLimit"/> and
    /// <see cref="GetSupportedCurrencies"/> are read-only and only require a validly-authenticated caller
    /// (the standard <c>openid</c> scope) - no <c>manage-limits</c> claim needed.
    /// </summary>
    /// <remarks>
    /// No endpoint here ever accepts the caller's Google/Microsoft access token as a parameter - callers
    /// authenticate with the Biatec bearer token alone. The provider token is resolved entirely from the
    /// encrypted copy cached inside that bearer token itself (the <c>provider_token</c> claim - see
    /// <see cref="ProviderAccessTokenProtector"/> and <c>OIDC_INTEGRATION_GUIDE.md</c>'s "Provider access
    /// token caching" section), decrypted here, in-memory, for the duration of this one request only -
    /// never logged, never persisted by this controller, never sent back to the caller. This is what lets
    /// the same Biatec token be used from any device/process, not just the one the user originally signed
    /// in on. If no provider token was ever cached (or it's since gone stale), the call fails with 401
    /// <c>storage_access_denied</c>; the caller needs a fresh interactive sign-in through
    /// <c>/authorize</c>, not a parameter it can pass here.
    /// </remarks>
    [ApiController]
    [Route("wallet")]
    public class WalletController : ControllerBase
    {
        private readonly IJwtIssuerService _jwtIssuerService;
        private readonly IWalletService _walletService;
        private readonly ISpendingLimitService _spendingLimitService;
        private readonly IExchangeRateService _exchangeRateService;
        private readonly IProviderAccessTokenProtector _providerTokenProtector;
        private readonly ICloudStorageProviderCatalog _providerCatalog;
        private readonly ILogger<WalletController> _logger;

        public WalletController(
            IJwtIssuerService jwtIssuerService,
            IWalletService walletService,
            ISpendingLimitService spendingLimitService,
            IExchangeRateService exchangeRateService,
            IProviderAccessTokenProtector providerTokenProtector,
            ICloudStorageProviderCatalog providerCatalog,
            ILogger<WalletController> logger)
        {
            _jwtIssuerService = jwtIssuerService;
            _walletService = walletService;
            _spendingLimitService = spendingLimitService;
            _exchangeRateService = exchangeRateService;
            _providerTokenProtector = providerTokenProtector;
            _providerCatalog = providerCatalog;
            _logger = logger;
        }

        /// <summary>
        /// Signs an Algorand transaction group. The group's total USD value (every payment/asset-transfer
        /// in it, priced via the Biatec Router) is checked against the caller's daily/weekly/monthly
        /// spending limits before anything is signed.
        /// </summary>
        /// <param name="request">The transactions to sign (base64 msgpack).</param>
        /// <returns>The signed transactions (base64 msgpack), in the same order as the request.</returns>
        /// <response code="200">All transactions were within limit and signed successfully.</response>
        /// <response code="400">The request was malformed, or a transaction could not be decoded.</response>
        /// <response code="401">The bearer token is missing, invalid, or expired.</response>
        /// <response code="403">The token lacks the <c>sign</c> claim, or the group exceeds the caller's spending limit.</response>
        /// <response code="503">A spent asset's USD value, or the caller's limit currency's exchange rate, could not be determined.</response>
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
            var accessToken = ResolveProviderAccessToken(principal, email);

            try
            {
                var signed = await ExecuteWithProviderTokenRefreshAsync(principal, email, provider, accessToken,
                    token => _walletService.SignTransactionGroupAsync(email, provider, decodedTransactions, token));
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
            catch (AssetValuationException ex)
            {
                return StatusCode(StatusCodes.Status503ServiceUnavailable, new ProblemDetails { Title = "asset_valuation_failed", Detail = ex.Message });
            }
            catch (UnsupportedCurrencyException ex)
            {
                return StatusCode(StatusCodes.Status503ServiceUnavailable, new ProblemDetails { Title = "spending_limit_currency_unavailable", Detail = ex.Message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error signing transaction group for {Email}.", email);
                return StatusCode(StatusCodes.Status500InternalServerError, new ProblemDetails { Title = "server_error", Detail = "Unable to sign the transaction group." });
            }
        }

        /// <summary>Returns the caller's current daily/weekly/monthly spending limits and their currency.</summary>
        /// <response code="200">The current limits (all-zero/unbounded, in USD, if never configured).</response>
        /// <response code="401">The bearer token is missing, invalid, or expired, or no cached provider
        /// access token is available (see the class remarks) - a fresh interactive sign-in is required.</response>
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
            var provider = principal.FindFirstValue(AuthSchemeNames.IdpClaimType) ?? string.Empty;
            var resolvedAccessToken = ResolveProviderAccessToken(principal, email);

            try
            {
                var settings = await ExecuteWithProviderTokenRefreshAsync(principal, email, provider, resolvedAccessToken,
                    token => _spendingLimitService.GetLimitsAsync(email, provider, token));
                return Ok(ToResponse(settings));
            }
            catch (UnauthorizedAccessException ex)
            {
                return StatusCode(StatusCodes.Status401Unauthorized, new ProblemDetails { Title = "storage_access_denied", Detail = ex.Message });
            }
        }

        /// <summary>Sets the caller's daily/weekly/monthly spending limits and the currency they're expressed in.</summary>
        /// <param name="request">The new limits (<c>0</c> to leave a window unbounded) and their currency.</param>
        /// <response code="200">The limits were updated.</response>
        /// <response code="400">The requested currency isn't supported - see <c>GET /wallet/limits/currencies</c>.</response>
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
            var provider = principal.FindFirstValue(AuthSchemeNames.IdpClaimType) ?? string.Empty;
            var accessToken = ResolveProviderAccessToken(principal, email);

            var settings = new SpendingLimitSettings
            {
                CurrencyCode = string.IsNullOrWhiteSpace(request.CurrencyCode) ? "USD" : request.CurrencyCode,
                DailyLimit = request.DailyLimit,
                WeeklyLimit = request.WeeklyLimit,
                MonthlyLimit = request.MonthlyLimit
            };

            try
            {
                await ExecuteWithProviderTokenRefreshAsync(principal, email, provider, accessToken,
                    token => _spendingLimitService.SetLimitsAsync(email, provider, token, settings));
            }
            catch (UnsupportedCurrencyException ex)
            {
                return BadRequest(new ProblemDetails { Title = "unsupported_currency", Detail = ex.Message });
            }
            catch (UnauthorizedAccessException ex)
            {
                return StatusCode(StatusCodes.Status401Unauthorized, new ProblemDetails { Title = "storage_access_denied", Detail = ex.Message });
            }

            _logger.LogInformation("{Email} updated their spending limits (currency {Currency}).", email, settings.CurrencyCode);
            return Ok(ToResponse(settings));
        }

        /// <summary>
        /// Lists every currency a spending limit can be configured in, with its current USD exchange rate.
        /// Rates come from the Czech National Bank's daily fixing and are cached, so they reflect the most
        /// recent published fixing rather than a live market feed.
        /// </summary>
        /// <response code="200">The supported currencies and their current USD rates.</response>
        /// <response code="401">The bearer token is missing, invalid, or expired.</response>
        /// <response code="503">The exchange rate feed could not be reached.</response>
        [AllowAnonymous]
        [RequiresBearerToken]
        [HttpGet("limits/currencies")]
        public async Task<IActionResult> GetSupportedCurrencies()
        {
            var authError = TryAuthenticate(requiredClaim: null, out _);
            if (authError != null)
            {
                return authError;
            }

            try
            {
                var currencies = await _exchangeRateService.GetSupportedCurrenciesAsync();
                return Ok(new SupportedCurrenciesResponse
                {
                    Currencies = currencies
                        .Select(c => new CurrencyRateResponse { Code = c.Code, Name = c.DisplayName, UsdPerUnit = c.UsdPerUnit })
                        .ToList()
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unable to fetch the supported currency list.");
                return StatusCode(StatusCodes.Status503ServiceUnavailable, new ProblemDetails { Title = "exchange_rate_unavailable", Detail = "Unable to fetch current exchange rates." });
            }
        }

        /// <summary>
        /// The provider access token to use for this call, decrypted from the bearer token's own
        /// <c>provider_token</c> claim (see the class remarks) - callers never supply this themselves.
        /// Returns <c>null</c> if no provider token was ever cached, or it can no longer be decrypted
        /// (protection key rotated, tampered, etc.) - already handled by every caller as "no storage
        /// access" (401).
        /// </summary>
        private string? ResolveProviderAccessToken(ClaimsPrincipal principal, string email)
        {
            var cachedProtectedToken = principal.FindFirstValue(ProviderAccessTokenProtector.ClaimType);
            return _providerTokenProtector.Unprotect(cachedProtectedToken, email);
        }

        /// <summary>
        /// Runs <paramref name="action"/> with <paramref name="accessToken"/>; if it fails because the
        /// cached provider access token has gone stale mid-lifetime of an otherwise still-valid Biatec
        /// token (<see cref="UnauthorizedAccessException"/>, e.g. "Google Drive access denied... may be
        /// expired"), renews it once from the bearer token's cached <c>provider_refresh_token</c> claim
        /// (see <see cref="ProviderAccessTokenProtector.RefreshClaimType"/>) and retries exactly once. The
        /// renewed token is used for this request only - it can't be written back into the caller's
        /// already-issued, signed bearer token, so the next call still resolves the original (stale)
        /// cached access token; the durable fix is <c>JwtIssuerService.RenewProviderTokenAsync</c>, which
        /// refreshes it every time the caller renews its Biatec refresh token. If there's no cached
        /// provider refresh token (or renewing it fails), the original exception is rethrown unchanged -
        /// no behavior change for callers who signed in before this existed.
        /// </summary>
        private async Task<T> ExecuteWithProviderTokenRefreshAsync<T>(
            ClaimsPrincipal principal,
            string email,
            string provider,
            string? accessToken,
            Func<string?, Task<T>> action)
        {
            try
            {
                return await action(accessToken);
            }
            catch (UnauthorizedAccessException)
            {
                var refreshedToken = await TryRefreshProviderAccessTokenAsync(principal, email, provider);
                if (string.IsNullOrWhiteSpace(refreshedToken))
                {
                    throw;
                }

                return await action(refreshedToken);
            }
        }

        /// <summary>Same retry-once behavior as the generic overload, for actions with no return value.</summary>
        private async Task ExecuteWithProviderTokenRefreshAsync(
            ClaimsPrincipal principal,
            string email,
            string provider,
            string? accessToken,
            Func<string?, Task> action)
        {
            try
            {
                await action(accessToken);
            }
            catch (UnauthorizedAccessException)
            {
                var refreshedToken = await TryRefreshProviderAccessTokenAsync(principal, email, provider);
                if (string.IsNullOrWhiteSpace(refreshedToken))
                {
                    throw;
                }

                await action(refreshedToken);
            }
        }

        /// <summary>
        /// Exchanges the bearer token's cached <c>provider_refresh_token</c> claim (if any) for a fresh
        /// provider access token. Returns <c>null</c> - never throws - if there's no cached refresh token,
        /// it can't be decrypted, or the provider rejects it (revoked/expired); callers treat that as "no
        /// renewal possible" and let the original error surface.
        /// </summary>
        private async Task<string?> TryRefreshProviderAccessTokenAsync(ClaimsPrincipal principal, string email, string provider)
        {
            var cachedProtectedRefreshToken = principal.FindFirstValue(ProviderAccessTokenProtector.RefreshClaimType);
            var providerRefreshToken = _providerTokenProtector.Unprotect(cachedProtectedRefreshToken, email);
            if (string.IsNullOrWhiteSpace(providerRefreshToken))
            {
                return null;
            }

            var storageProvider = _providerCatalog.Resolve(provider);
            var refreshed = await storageProvider.RefreshAccessTokenAsync(providerRefreshToken);
            return refreshed?.AccessToken;
        }

        private static SpendingLimitResponse ToResponse(SpendingLimitSettings settings) => new()
        {
            CurrencyCode = settings.CurrencyCode,
            DailyLimit = settings.DailyLimit,
            WeeklyLimit = settings.WeeklyLimit,
            MonthlyLimit = settings.MonthlyLimit
        };

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
