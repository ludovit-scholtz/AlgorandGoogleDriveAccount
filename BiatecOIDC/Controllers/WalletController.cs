using System.Security.Claims;
using BiatecOIDC.BusinessLogic;
using BiatecOIDC.Helper;
using BiatecOIDC.Model;
using BiatecOIDC.Swagger;
using BiatecSelfCustodyCore.Model;
using BiatecSelfCustodyCore.Providers;
using BiatecSelfCustodyCore.Repository;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BiatecOIDC.Controllers
{
    /// <summary>
    /// Self-custody wallet API: signs Algorand transaction groups and manages the caller's
    /// daily/weekly/monthly spending limits and seed vault. Every endpoint here requires a Biatec OIDC
    /// access token (<c>Authorization: Bearer</c>). State-changing endpoints additionally require the
    /// relevant scope-derived claim - <c>sign</c> for <see cref="SignTransactionGroup"/> (any transaction),
    /// <c>rekey</c> additionally for <see cref="SignTransactionGroup"/> when the group contains a rekey
    /// transaction (the strictest claim Biatec issues - see <c>JwtIssuerService.WalletApiScopes</c>'s
    /// remarks), <c>manage-limits</c> for <see cref="UpdateSpendingLimit"/> - exactly as granted at
    /// <c>/authorize</c> (see <c>JwtIssuerService.CreateAccessToken</c>); a token missing the claim is
    /// rejected with 403, never silently treated as authorized. <see cref="GetSpendingLimit"/> and
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
        private readonly ICloudAccountRepository _accountRepository;
        private readonly ILogger<WalletController> _logger;

        public WalletController(
            IJwtIssuerService jwtIssuerService,
            IWalletService walletService,
            ISpendingLimitService spendingLimitService,
            IExchangeRateService exchangeRateService,
            IProviderAccessTokenProtector providerTokenProtector,
            ICloudStorageProviderCatalog providerCatalog,
            ICloudAccountRepository accountRepository,
            ILogger<WalletController> logger)
        {
            _jwtIssuerService = jwtIssuerService;
            _walletService = walletService;
            _spendingLimitService = spendingLimitService;
            _exchangeRateService = exchangeRateService;
            _providerTokenProtector = providerTokenProtector;
            _providerCatalog = providerCatalog;
            _accountRepository = accountRepository;
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
        /// <response code="403">The token lacks the <c>sign</c> claim, lacks the <c>rekey</c> claim while the
        /// group contains a rekey transaction, or the group exceeds the caller's spending limit.</response>
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

            // Rekey is gated on its own, stricter claim - independent of (and in addition to) `sign` above -
            // since a rekey transaction permanently reassigns which key controls the account, unlike a
            // pay/axfer which is bounded by the spending limit. A group containing even one rekey
            // transaction is refused outright if the caller's token lacks it; the rest of the group (if
            // any) never gets a chance to partially sign.
            var containsRekey = decodedTransactions.Any(tx =>
            {
                try
                {
                    return AlgorandTransactionInspector.Inspect(tx).IsRekey;
                }
                catch (FormatException)
                {
                    return false; // Undecodable transactions are reported by WalletService below, not here.
                }
            });
            if (containsRekey && !string.Equals(principal!.FindFirstValue(WalletScopes.Rekey), "true", StringComparison.Ordinal))
            {
                return StatusCode(StatusCodes.Status403Forbidden, new ProblemDetails
                {
                    Title = "insufficient_scope",
                    Detail = "This transaction group contains a rekey transaction, which requires the 'rekey' scope/claim - the token presented does not have it."
                });
            }

            var email = principal!.FindFirstValue(ClaimTypes.Email)!;
            var provider = principal.FindFirstValue(AuthSchemeNames.IdpClaimType) ?? string.Empty;
            var accessToken = ResolveProviderAccessToken(principal, email);

            try
            {
                var signed = await ExecuteWithProviderTokenRefreshAsync(principal, email, provider, accessToken,
                    token => _walletService.SignTransactionGroupAsync(email, provider, decodedTransactions, token, request.PrimaryAddress, request.Slot));
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
            catch (InvalidOperationException ex)
            {
                return BadRequest(new ProblemDetails { Title = "seed_not_found", Detail = ex.Message });
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

        /// <summary>
        /// Returns the caller's current daily/weekly/monthly spending limits and their currency, for one
        /// bucket - the account-wide global bucket if <paramref name="primaryAddress"/> is omitted, or the
        /// per-address bucket for that <c>(primaryAddress, slot)</c> signing identity otherwise.
        /// </summary>
        /// <param name="primaryAddress">Selects the per-address bucket instead of the global one. Omit for the account-wide global bucket.</param>
        /// <param name="slot">ARC-76 slot of <paramref name="primaryAddress"/>'s bucket. Ignored if <paramref name="primaryAddress"/> is omitted.</param>
        /// <response code="200">The current limits (all-zero/unbounded, in USD, if never configured).</response>
        /// <response code="401">The bearer token is missing, invalid, or expired, or no cached provider
        /// access token is available (see the class remarks) - a fresh interactive sign-in is required.</response>
        [AllowAnonymous]
        [RequiresBearerToken]
        [HttpGet("limits")]
        public async Task<IActionResult> GetSpendingLimit([FromQuery] string? primaryAddress = null, [FromQuery] int slot = 0)
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
                    token => _spendingLimitService.GetLimitsAsync(email, provider, token, primaryAddress, slot));
                return Ok(ToResponse(settings, primaryAddress, slot));
            }
            catch (UnauthorizedAccessException ex)
            {
                return StatusCode(StatusCodes.Status401Unauthorized, new ProblemDetails { Title = "storage_access_denied", Detail = ex.Message });
            }
        }

        /// <summary>
        /// Sets the caller's daily/weekly/monthly spending limits and the currency they're expressed in,
        /// for one bucket - same <paramref name="primaryAddress"/>/<paramref name="slot"/> selector
        /// convention as <see cref="GetSpendingLimit"/>.
        /// </summary>
        /// <param name="request">The new limits (<c>0</c> to leave a window unbounded) and their currency.</param>
        /// <param name="primaryAddress">Selects the per-address bucket instead of the global one. Omit for the account-wide global bucket.</param>
        /// <param name="slot">ARC-76 slot of <paramref name="primaryAddress"/>'s bucket. Ignored if <paramref name="primaryAddress"/> is omitted.</param>
        /// <response code="200">The limits were updated.</response>
        /// <response code="400">The requested currency isn't supported - see <c>GET /wallet/limits/currencies</c>.</response>
        /// <response code="401">The bearer token is missing, invalid, or expired.</response>
        /// <response code="403">The token lacks the <c>manage-limits</c> claim.</response>
        [AllowAnonymous]
        [RequiresBearerToken]
        [HttpPut("limits")]
        public async Task<IActionResult> UpdateSpendingLimit([FromBody] UpdateSpendingLimitRequest request, [FromQuery] string? primaryAddress = null, [FromQuery] int slot = 0)
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
                    token => _spendingLimitService.SetLimitsAsync(email, provider, token, settings, primaryAddress, slot));
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
            return Ok(ToResponse(settings, primaryAddress, slot));
        }

        /// <summary>Lists every seed's identifying address in the caller's vault, and which one is primary.</summary>
        /// <response code="200">The caller's seed addresses.</response>
        /// <response code="401">The bearer token is missing, invalid, or expired, or no cached provider access token is available.</response>
        [AllowAnonymous]
        [RequiresBearerToken]
        [HttpGet("address")]
        public async Task<IActionResult> ListAddresses()
        {
            var authError = TryAuthenticate(requiredClaim: null, out var principal);
            if (authError != null)
            {
                return authError;
            }

            var email = principal!.FindFirstValue(ClaimTypes.Email)!;
            var provider = principal.FindFirstValue(AuthSchemeNames.IdpClaimType) ?? string.Empty;
            var accessToken = ResolveProviderAccessToken(principal, email);

            try
            {
                var seeds = await ExecuteWithProviderTokenRefreshAsync(principal, email, provider, accessToken,
                    token => _accountRepository.ListSeedsAsync(email, provider, token));
                return Ok(new ListAddressesResponse
                {
                    Addresses = seeds.Select(s => new AddressResponse { Address = s.PrimaryAddress, IsPrimary = s.IsPrimary }).ToList()
                });
            }
            catch (UnauthorizedAccessException ex)
            {
                return StatusCode(StatusCodes.Status401Unauthorized, new ProblemDetails { Title = "storage_access_denied", Detail = ex.Message });
            }
        }

        /// <summary>
        /// Derives (without signing anything) the ARC-76 address at <paramref name="slot"/> for the seed
        /// identified by <paramref name="primaryAddress"/> (see <see cref="ListAddresses"/>).
        /// </summary>
        /// <param name="primaryAddress">The seed's own identifying (slot-0) address.</param>
        /// <param name="slot">ARC-76 derivation index within that seed. Defaults to <c>0</c>.</param>
        /// <response code="200">The derived address.</response>
        /// <response code="400">No seed with that address exists in the caller's vault.</response>
        /// <response code="401">The bearer token is missing, invalid, or expired, or no cached provider access token is available.</response>
        [AllowAnonymous]
        [RequiresBearerToken]
        [HttpGet("address/{primaryAddress}/{slot:int?}")]
        public async Task<IActionResult> GetAddress(string primaryAddress, int? slot)
        {
            var authError = TryAuthenticate(requiredClaim: null, out var principal);
            if (authError != null)
            {
                return authError;
            }

            var email = principal!.FindFirstValue(ClaimTypes.Email)!;
            var provider = principal.FindFirstValue(AuthSchemeNames.IdpClaimType) ?? string.Empty;
            var accessToken = ResolveProviderAccessToken(principal, email);
            var resolvedSlot = slot ?? 0;

            try
            {
                var address = await ExecuteWithProviderTokenRefreshAsync(principal, email, provider, accessToken,
                    token => _accountRepository.DeriveAddressAsync(email, provider, primaryAddress, resolvedSlot, token));
                return Ok(new DerivedAddressResponse { Address = address, PrimaryAddress = primaryAddress, Slot = resolvedSlot });
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new ProblemDetails { Title = "seed_not_found", Detail = ex.Message });
            }
            catch (UnauthorizedAccessException ex)
            {
                return StatusCode(StatusCodes.Status401Unauthorized, new ProblemDetails { Title = "storage_access_denied", Detail = ex.Message });
            }
        }

        /// <summary>
        /// Lists every seed's EVM address in the caller's vault, and which one is primary. Same seeds as
        /// <see cref="ListAddresses"/> - just derived via <c>ARC76.GetEVMEmailAccount</c> instead of
        /// <c>ARC76.GetEmailAccount</c>, so it's the same address across every EVM chain (Ethereum, Gnosis,
        /// Arbitrum, Base, ...) - there is no per-chain concept at this layer.
        /// </summary>
        /// <response code="200">The caller's seed EVM addresses.</response>
        /// <response code="401">The bearer token is missing, invalid, or expired, or no cached provider access token is available.</response>
        [AllowAnonymous]
        [RequiresBearerToken]
        [HttpGet("evm/address")]
        public async Task<IActionResult> ListEvmAddresses()
        {
            var authError = TryAuthenticate(requiredClaim: null, out var principal);
            if (authError != null)
            {
                return authError;
            }

            var email = principal!.FindFirstValue(ClaimTypes.Email)!;
            var provider = principal.FindFirstValue(AuthSchemeNames.IdpClaimType) ?? string.Empty;
            var accessToken = ResolveProviderAccessToken(principal, email);

            try
            {
                var seeds = await ExecuteWithProviderTokenRefreshAsync(principal, email, provider, accessToken,
                    token => _accountRepository.ListSeedsAsync(email, provider, token));
                var addresses = new List<EvmAddressResponse>();
                foreach (var seed in seeds)
                {
                    var evmAddress = await ExecuteWithProviderTokenRefreshAsync(principal, email, provider, accessToken,
                        token => _accountRepository.DeriveEvmAddressAsync(email, provider, seed.PrimaryAddress, 0, token));
                    addresses.Add(new EvmAddressResponse { Address = evmAddress, IsPrimary = seed.IsPrimary });
                }

                return Ok(new ListEvmAddressesResponse { Addresses = addresses });
            }
            catch (UnauthorizedAccessException ex)
            {
                return StatusCode(StatusCodes.Status401Unauthorized, new ProblemDetails { Title = "storage_access_denied", Detail = ex.Message });
            }
        }

        /// <summary>
        /// Derives (without signing anything) the EVM address at <paramref name="slot"/> for the seed
        /// identified by <paramref name="primaryAddress"/> (see <see cref="ListEvmAddresses"/>).
        /// </summary>
        /// <param name="primaryAddress">The seed's own identifying (Algorand slot-0) address.</param>
        /// <param name="slot">ARC-76 derivation index within that seed. Defaults to <c>0</c>.</param>
        /// <response code="200">The derived EVM address.</response>
        /// <response code="400">No seed with that address exists in the caller's vault.</response>
        /// <response code="401">The bearer token is missing, invalid, or expired, or no cached provider access token is available.</response>
        [AllowAnonymous]
        [RequiresBearerToken]
        [HttpGet("evm/address/{primaryAddress}/{slot:int?}")]
        public async Task<IActionResult> GetEvmAddress(string primaryAddress, int? slot)
        {
            var authError = TryAuthenticate(requiredClaim: null, out var principal);
            if (authError != null)
            {
                return authError;
            }

            var email = principal!.FindFirstValue(ClaimTypes.Email)!;
            var provider = principal.FindFirstValue(AuthSchemeNames.IdpClaimType) ?? string.Empty;
            var accessToken = ResolveProviderAccessToken(principal, email);
            var resolvedSlot = slot ?? 0;

            try
            {
                var address = await ExecuteWithProviderTokenRefreshAsync(principal, email, provider, accessToken,
                    token => _accountRepository.DeriveEvmAddressAsync(email, provider, primaryAddress, resolvedSlot, token));
                return Ok(new DerivedEvmAddressResponse { Address = address, PrimaryAddress = primaryAddress, Slot = resolvedSlot });
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new ProblemDetails { Title = "seed_not_found", Detail = ex.Message });
            }
            catch (UnauthorizedAccessException ex)
            {
                return StatusCode(StatusCodes.Status401Unauthorized, new ProblemDetails { Title = "storage_access_denied", Detail = ex.Message });
            }
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
        /// Lists every seed ever generated for the caller. Never includes a mnemonic - only each seed's
        /// identifying address, creation date, and whether it's currently primary.
        /// </summary>
        /// <response code="200">The caller's seeds.</response>
        /// <response code="401">The bearer token is missing, invalid, or expired, or no cached provider access token is available.</response>
        [AllowAnonymous]
        [RequiresBearerToken]
        [HttpGet("seeds")]
        public async Task<IActionResult> ListSeeds()
        {
            var authError = TryAuthenticate(requiredClaim: null, out var principal);
            if (authError != null)
            {
                return authError;
            }

            var email = principal!.FindFirstValue(ClaimTypes.Email)!;
            var provider = principal.FindFirstValue(AuthSchemeNames.IdpClaimType) ?? string.Empty;
            var accessToken = ResolveProviderAccessToken(principal, email);

            try
            {
                var seeds = await ExecuteWithProviderTokenRefreshAsync(principal, email, provider, accessToken,
                    token => _accountRepository.ListSeedsAsync(email, provider, token));
                return Ok(new ListSeedsResponse
                {
                    Seeds = seeds.Select(s => new SeedResponse { Address = s.PrimaryAddress, CreatedUtc = s.CreatedUtc, IsPrimary = s.IsPrimary }).ToList()
                });
            }
            catch (UnauthorizedAccessException ex)
            {
                return StatusCode(StatusCodes.Status401Unauthorized, new ProblemDetails { Title = "storage_access_denied", Detail = ex.Message });
            }
        }

        /// <summary>
        /// Generates a brand-new, independent seed and adds it to the caller's vault. Existing seeds are
        /// never removed - the new seed starts out non-primary (unless it's the caller's very first seed
        /// ever) until <see cref="SwitchPrimarySeed"/> is called explicitly, so this alone never changes
        /// what Biatec currently signs with. Intended as the first step of recovering from a suspected key
        /// compromise: mint a new seed here, then have the client build and submit (via
        /// <see cref="SignTransactionGroup"/>, which requires the <c>rekey</c> claim) an on-chain rekey
        /// transaction pointing at its address, then finally call <see cref="SwitchPrimarySeed"/> once
        /// that's confirmed.
        /// </summary>
        /// <response code="200">The newly-created seed.</response>
        /// <response code="401">The bearer token is missing, invalid, or expired, or no cached provider access token is available.</response>
        /// <response code="403">The token lacks the <c>rekey</c> claim.</response>
        [AllowAnonymous]
        [RequiresBearerToken]
        [HttpPost("seeds")]
        public async Task<IActionResult> CreateSeed()
        {
            var authError = TryAuthenticate(WalletScopes.Rekey, out var principal);
            if (authError != null)
            {
                return authError;
            }

            var email = principal!.FindFirstValue(ClaimTypes.Email)!;
            var provider = principal.FindFirstValue(AuthSchemeNames.IdpClaimType) ?? string.Empty;
            var accessToken = ResolveProviderAccessToken(principal, email);

            try
            {
                var seed = await ExecuteWithProviderTokenRefreshAsync(principal, email, provider, accessToken,
                    token => _accountRepository.CreateSeedAsync(email, provider, token));
                return Ok(new SeedResponse { Address = seed.PrimaryAddress, CreatedUtc = seed.CreatedUtc, IsPrimary = seed.IsPrimary });
            }
            catch (VaultConcurrencyConflictException ex)
            {
                return StatusCode(StatusCodes.Status409Conflict, new ProblemDetails { Title = "vault_concurrency_conflict", Detail = ex.Message });
            }
            catch (UnauthorizedAccessException ex)
            {
                return StatusCode(StatusCodes.Status401Unauthorized, new ProblemDetails { Title = "storage_access_denied", Detail = ex.Message });
            }
        }

        /// <summary>
        /// Switches which seed in the caller's vault is primary - the one <see cref="SignTransactionGroup"/>
        /// and other signing operations derive from going forward. Does not touch the blockchain itself -
        /// call this only after an on-chain rekey transaction naming the new seed's address has actually
        /// been confirmed, otherwise Biatec would start signing with a key the account no longer recognizes.
        /// </summary>
        /// <param name="request">The address of the seed (see <c>GET /wallet/seeds</c>) to make primary.</param>
        /// <response code="200">The seed is now primary.</response>
        /// <response code="400">No seed with that address exists in the caller's vault.</response>
        /// <response code="401">The bearer token is missing, invalid, or expired, or no cached provider access token is available.</response>
        /// <response code="403">The token lacks the <c>sign</c> claim.</response>
        [AllowAnonymous]
        [RequiresBearerToken]
        [HttpPut("seeds/primary")]
        public async Task<IActionResult> SwitchPrimarySeed([FromBody] SwitchPrimarySeedRequest request)
        {
            var authError = TryAuthenticate(WalletScopes.Sign, out var principal);
            if (authError != null)
            {
                return authError;
            }

            if (string.IsNullOrWhiteSpace(request.Address))
            {
                return BadRequest(new ProblemDetails { Title = "invalid_request", Detail = "Address is required." });
            }

            var email = principal!.FindFirstValue(ClaimTypes.Email)!;
            var provider = principal.FindFirstValue(AuthSchemeNames.IdpClaimType) ?? string.Empty;
            var accessToken = ResolveProviderAccessToken(principal, email);

            try
            {
                await ExecuteWithProviderTokenRefreshAsync(principal, email, provider, accessToken,
                    token => _accountRepository.SwitchPrimarySeedAsync(email, provider, request.Address, token));
                return Ok();
            }
            catch (VaultConcurrencyConflictException ex)
            {
                return StatusCode(StatusCodes.Status409Conflict, new ProblemDetails { Title = "vault_concurrency_conflict", Detail = ex.Message });
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new ProblemDetails { Title = "seed_not_found", Detail = ex.Message });
            }
            catch (UnauthorizedAccessException ex)
            {
                return StatusCode(StatusCodes.Status401Unauthorized, new ProblemDetails { Title = "storage_access_denied", Detail = ex.Message });
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

        private static SpendingLimitResponse ToResponse(SpendingLimitSettings settings, string? primaryAddress, int slot) => new()
        {
            CurrencyCode = settings.CurrencyCode,
            DailyLimit = settings.DailyLimit,
            WeeklyLimit = settings.WeeklyLimit,
            MonthlyLimit = settings.MonthlyLimit,
            PrimaryAddress = primaryAddress,
            Slot = slot
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
            public const string Rekey = "rekey";
        }
    }
}
