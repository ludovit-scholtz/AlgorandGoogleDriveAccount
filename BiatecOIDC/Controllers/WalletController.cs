using System.Security.Claims;
using System.Text.Json;
using Algorand;
using Algorand.Algod;
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
        private readonly IAddressActivationService _addressActivationService;
        private readonly INetworkResolver _networkResolver;
        private readonly ILogger<WalletController> _logger;

        public WalletController(
            IJwtIssuerService jwtIssuerService,
            IWalletService walletService,
            ISpendingLimitService spendingLimitService,
            IExchangeRateService exchangeRateService,
            IProviderAccessTokenProtector providerTokenProtector,
            ICloudStorageProviderCatalog providerCatalog,
            ICloudAccountRepository accountRepository,
            IAddressActivationService addressActivationService,
            INetworkResolver networkResolver,
            ILogger<WalletController> logger)
        {
            _jwtIssuerService = jwtIssuerService;
            _walletService = walletService;
            _spendingLimitService = spendingLimitService;
            _exchangeRateService = exchangeRateService;
            _providerTokenProtector = providerTokenProtector;
            _providerCatalog = providerCatalog;
            _accountRepository = accountRepository;
            _addressActivationService = addressActivationService;
            _networkResolver = networkResolver;
            _logger = logger;
        }

        /// <summary>
        /// Signs a transaction group as <paramref name="address"/> on <paramref name="network"/> - Algorand-family
        /// (AVM) and Ethereum-family (EVM) chains are both supported. For AVM, the group's total USD value
        /// (every payment/asset-transfer in it, priced via the Biatec Router) is checked against the
        /// caller's daily/weekly/monthly spending limits before anything is signed; EVM native-currency
        /// spending limits aren't implemented yet, so no such check happens for an EVM group (see
        /// <see cref="IWalletService.SignEvmTransactionGroupAsync"/>'s remarks).
        /// </summary>
        /// <param name="network">Which chain <paramref name="address"/> belongs to (e.g. <c>algorand</c>, <c>voi</c>, <c>ethereum</c>, <c>arbitrum</c>) - see <c>GET /chains</c>.</param>
        /// <param name="address">
        /// Which identity signs - a seed's own primary address, an address previously derived via
        /// <c>GET /wallet/address/{seedAddress}/{slot}</c>, or an address activated via
        /// <c>POST /wallet/{network}/{address}/activate</c> (e.g. after an on-chain rekey to a
        /// Biatec-controlled key).
        /// </param>
        /// <param name="request">
        /// The transactions to sign - base64-encoded msgpack for an AVM <paramref name="network"/>, or
        /// base64-encoded RLP for an EVM one (the type - legacy, EIP-2930, EIP-1559, EIP-7702 - is read off
        /// the wire, same as <c>Nethereum.Model.TransactionFactory.CreateTransaction</c>).
        /// </param>
        /// <returns>The signed transactions, in the same encoding as the request, in the same order.</returns>
        /// <response code="200">All transactions were within limit (AVM) and signed successfully.</response>
        /// <response code="400">The request was malformed, a transaction could not be decoded, an AVM
        /// transaction's sender doesn't match <paramref name="address"/>, or <paramref name="address"/> is
        /// unknown (never derived or activated).</response>
        /// <response code="401">The bearer token is missing, invalid, or expired.</response>
        /// <response code="403">The token lacks the <c>sign</c> claim, lacks the <c>rekey</c> claim while the
        /// group contains an AVM rekey transaction, or the group exceeds the caller's spending limit.</response>
        /// <response code="503">A spent asset's USD value, or the caller's limit currency's exchange rate, could not be determined.</response>
        [AllowAnonymous]
        [RequiresBearerToken]
        [HttpPost("{network}/{address}/sign")]
        [ProducesResponseType(typeof(SignTransactionGroupResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
        public async Task<IActionResult> SignTransactionGroup(string network, string address, [FromBody] SignTransactionGroupRequest request)
        {
            var authError = TryAuthenticate(WalletScopes.Sign, out var principal);
            if (authError != null)
            {
                return authError;
            }

            var resolvedNetwork = await _networkResolver.ResolveAsync(network);
            if (resolvedNetwork == null)
            {
                return BadRequest(new ProblemDetails { Title = "unknown_network", Detail = $"Unknown network '{network}'. See GET /chains for currently-supported networks." });
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

            if (resolvedNetwork.Family == ChainFamily.Evm)
            {
                return await SignEvmTransactionGroupAsync(principal!, network, address, decodedTransactions);
            }

            var infos = new List<AlgorandTransferInfo>();
            foreach (var tx in decodedTransactions)
            {
                try
                {
                    infos.Add(AlgorandTransactionInspector.Inspect(tx));
                }
                catch (FormatException)
                {
                    infos.Add(null!); // Undecodable transactions are reported by WalletService below, not here.
                }
            }

            // Defense in depth: a plain (non-multisig) transaction's own sender must match the identity this
            // route names - catches an integration bug (signing under the wrong route address) before it
            // produces a signature that would just be rejected on-chain anyway. A multisig envelope's
            // "sender" is the multisig group's address, not the cosigning participant's own - not checked.
            var senderMismatch = infos.FirstOrDefault(i => i != null && !i.IsMultisig && !string.IsNullOrEmpty(i.Sender) && !string.Equals(i.Sender, address, StringComparison.Ordinal));
            if (senderMismatch != null)
            {
                return BadRequest(new ProblemDetails
                {
                    Title = "sender_mismatch",
                    Detail = $"A transaction's sender ('{senderMismatch.Sender}') does not match the address '{address}' in the route."
                });
            }

            // Rekey is gated on its own, stricter claim - independent of (and in addition to) `sign` above -
            // since a rekey transaction permanently reassigns which key controls the account, unlike a
            // pay/axfer which is bounded by the spending limit. A group containing even one rekey
            // transaction is refused outright if the caller's token lacks it; the rest of the group (if
            // any) never gets a chance to partially sign.
            var containsRekey = infos.Any(i => i != null && i.IsRekey);
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

            var signer = await ResolveSignerAsync(principal, email, provider, accessToken, address);
            if (signer == null)
            {
                return BadRequest(new ProblemDetails
                {
                    Title = "address_not_active",
                    Detail = $"'{address}' is not a known address for this account. Derive it via GET /wallet/address/{{seedAddress}}/{{slot}}, or activate an externally rekeyed address via POST /wallet/{network}/{address}/activate first."
                });
            }

            try
            {
                var signed = await ExecuteWithProviderTokenRefreshAsync(principal, email, provider, accessToken,
                    token => _walletService.SignTransactionGroupAsync(email, provider, decodedTransactions, token, signer.Value.SeedAddress, signer.Value.Slot));
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
        /// EVM branch of <see cref="SignTransactionGroup"/> - no <see cref="AlgorandTransactionInspector"/>
        /// (that's AVM-msgpack-specific), no sender/rekey checks (an unsigned EVM transaction carries no
        /// sender field at all - the sender is *derived* from whichever key signs it, unlike Algorand's
        /// explicit <c>snd</c> field - and EVM has no rekey concept), and no spending-limit enforcement (not
        /// implemented for EVM yet - see <see cref="IWalletService.SignEvmTransactionGroupAsync"/>).
        /// </summary>
        private async Task<IActionResult> SignEvmTransactionGroupAsync(ClaimsPrincipal principal, string network, string address, List<byte[]> decodedTransactions)
        {
            var email = principal.FindFirstValue(ClaimTypes.Email)!;
            var provider = principal.FindFirstValue(AuthSchemeNames.IdpClaimType) ?? string.Empty;
            var accessToken = ResolveProviderAccessToken(principal, email);

            var signer = await ResolveSignerAsync(principal, email, provider, accessToken, address);
            if (signer == null)
            {
                return BadRequest(new ProblemDetails
                {
                    Title = "address_not_active",
                    Detail = $"'{address}' is not a known address for this account. Derive it via GET /wallet/address/{{seedAddress}}/{{slot}}, or activate it via POST /wallet/{network}/{address}/activate first."
                });
            }

            List<EvmUnsignedTransaction> unsignedTransactions;
            try
            {
                unsignedTransactions = decodedTransactions
                    .Select(tx => EvmTransactionRequestParser.Parse(JsonSerializer.Deserialize<EvmTransactionRequest>(tx) ?? throw new FormatException("Empty EVM transaction request.")))
                    .ToList();
            }
            catch (Exception ex) when (ex is FormatException or JsonException)
            {
                return BadRequest(new ProblemDetails { Title = "invalid_request", Detail = $"Unable to parse EVM transaction request: {ex.Message}" });
            }

            try
            {
                var signed = await ExecuteWithProviderTokenRefreshAsync(principal, email, provider, accessToken,
                    token => _walletService.SignEvmTransactionGroupAsync(email, provider, unsignedTransactions, token, signer.Value.SeedAddress, signer.Value.Slot));
                return Ok(new SignTransactionGroupResponse
                {
                    SignedTransactions = signed.Select(Convert.ToBase64String).ToList()
                });
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
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error signing an EVM transaction group for {Email}.", email);
                return StatusCode(StatusCodes.Status500InternalServerError, new ProblemDetails { Title = "server_error", Detail = "Unable to sign the transaction group." });
            }
        }

        /// <summary>Returns the caller's current daily/weekly/monthly spending limits and their currency, for the account-wide global bucket.</summary>
        /// <response code="200">The current limits (all-zero/unbounded, in USD, if never configured).</response>
        /// <response code="401">The bearer token is missing, invalid, or expired, or no cached provider
        /// access token is available (see the class remarks) - a fresh interactive sign-in is required.</response>
        [AllowAnonymous]
        [RequiresBearerToken]
        [HttpGet("limits")]
        [ProducesResponseType(typeof(SpendingLimitResponse), StatusCodes.Status200OK)]
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
                    token => _spendingLimitService.GetLimitsAsync(email, provider, token, seedAddress: null, slot: 0));
                return Ok(ToResponse(settings, address: null, network: null, seedAddress: null, slot: 0));
            }
            catch (UnauthorizedAccessException ex)
            {
                return StatusCode(StatusCodes.Status401Unauthorized, new ProblemDetails { Title = "storage_access_denied", Detail = ex.Message });
            }
        }

        /// <summary>Sets the caller's daily/weekly/monthly spending limits and the currency they're expressed in, for the account-wide global bucket.</summary>
        /// <param name="request">The new limits (<c>0</c> to leave a window unbounded) and their currency.</param>
        /// <response code="200">The limits were updated.</response>
        /// <response code="400">The requested currency isn't supported - see <c>GET /wallet/limits/currencies</c>.</response>
        /// <response code="401">The bearer token is missing, invalid, or expired.</response>
        /// <response code="403">The token lacks the <c>manage-limits</c> claim.</response>
        [AllowAnonymous]
        [RequiresBearerToken]
        [HttpPut("limits")]
        [ProducesResponseType(typeof(SpendingLimitResponse), StatusCodes.Status200OK)]
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
                    token => _spendingLimitService.SetLimitsAsync(email, provider, token, settings, seedAddress: null, slot: 0));
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
            return Ok(ToResponse(settings, address: null, network: null, seedAddress: null, slot: 0));
        }

        /// <summary>Returns the caller's current daily/weekly/monthly spending limits for the bucket tied to <paramref name="address"/>.</summary>
        /// <param name="network">Which chain <paramref name="address"/> belongs to - see <c>GET /chains</c>.</param>
        /// <param name="address">A known address (native or activated) - see <see cref="SignTransactionGroup"/>'s remarks for how it resolves.</param>
        /// <response code="200">The current limits (all-zero/unbounded, in USD, if never configured).</response>
        /// <response code="400"><paramref name="address"/> is unknown, or <paramref name="network"/> is unrecognized.</response>
        /// <response code="401">The bearer token is missing, invalid, or expired, or no cached provider access token is available.</response>
        [AllowAnonymous]
        [RequiresBearerToken]
        [HttpGet("{network}/{address}/limits")]
        [ProducesResponseType(typeof(SpendingLimitResponse), StatusCodes.Status200OK)]
        public async Task<IActionResult> GetSpendingLimitForAddress(string network, string address)
        {
            var authError = TryAuthenticate(requiredClaim: null, out var principal);
            if (authError != null)
            {
                return authError;
            }

            if (await _networkResolver.ResolveAsync(network) == null)
            {
                return BadRequest(new ProblemDetails { Title = "unknown_network", Detail = $"Unknown network '{network}'." });
            }

            var email = principal!.FindFirstValue(ClaimTypes.Email)!;
            var provider = principal.FindFirstValue(AuthSchemeNames.IdpClaimType) ?? string.Empty;
            var resolvedAccessToken = ResolveProviderAccessToken(principal, email);

            var signer = await ResolveSignerAsync(principal, email, provider, resolvedAccessToken, address);
            if (signer == null)
            {
                return BadRequest(new ProblemDetails { Title = "address_not_active", Detail = $"'{address}' is not a known address for this account." });
            }

            try
            {
                var settings = await ExecuteWithProviderTokenRefreshAsync(principal, email, provider, resolvedAccessToken,
                    token => _spendingLimitService.GetLimitsAsync(email, provider, token, signer.Value.SeedAddress, signer.Value.Slot));
                return Ok(ToResponse(settings, address, network, signer.Value.SeedAddress, signer.Value.Slot));
            }
            catch (UnauthorizedAccessException ex)
            {
                return StatusCode(StatusCodes.Status401Unauthorized, new ProblemDetails { Title = "storage_access_denied", Detail = ex.Message });
            }
        }

        /// <summary>Sets the caller's daily/weekly/monthly spending limits for the bucket tied to <paramref name="address"/>.</summary>
        /// <param name="network">Which chain <paramref name="address"/> belongs to - see <c>GET /chains</c>.</param>
        /// <param name="address">A known address (native or activated).</param>
        /// <param name="request">The new limits (<c>0</c> to leave a window unbounded) and their currency.</param>
        /// <response code="200">The limits were updated.</response>
        /// <response code="400">The requested currency isn't supported, <paramref name="address"/> is unknown, or <paramref name="network"/> is unrecognized.</response>
        /// <response code="401">The bearer token is missing, invalid, or expired.</response>
        /// <response code="403">The token lacks the <c>manage-limits</c> claim.</response>
        [AllowAnonymous]
        [RequiresBearerToken]
        [HttpPut("{network}/{address}/limits")]
        [ProducesResponseType(typeof(SpendingLimitResponse), StatusCodes.Status200OK)]
        public async Task<IActionResult> UpdateSpendingLimitForAddress(string network, string address, [FromBody] UpdateSpendingLimitRequest request)
        {
            var authError = TryAuthenticate(WalletScopes.ManageLimits, out var principal);
            if (authError != null)
            {
                return authError;
            }

            if (await _networkResolver.ResolveAsync(network) == null)
            {
                return BadRequest(new ProblemDetails { Title = "unknown_network", Detail = $"Unknown network '{network}'." });
            }

            var email = principal!.FindFirstValue(ClaimTypes.Email)!;
            var provider = principal.FindFirstValue(AuthSchemeNames.IdpClaimType) ?? string.Empty;
            var accessToken = ResolveProviderAccessToken(principal, email);

            var signer = await ResolveSignerAsync(principal, email, provider, accessToken, address);
            if (signer == null)
            {
                return BadRequest(new ProblemDetails { Title = "address_not_active", Detail = $"'{address}' is not a known address for this account." });
            }

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
                    token => _spendingLimitService.SetLimitsAsync(email, provider, token, settings, signer.Value.SeedAddress, signer.Value.Slot));
            }
            catch (UnsupportedCurrencyException ex)
            {
                return BadRequest(new ProblemDetails { Title = "unsupported_currency", Detail = ex.Message });
            }
            catch (UnauthorizedAccessException ex)
            {
                return StatusCode(StatusCodes.Status401Unauthorized, new ProblemDetails { Title = "storage_access_denied", Detail = ex.Message });
            }

            _logger.LogInformation("{Email} updated their spending limits for {Address} on {Network} (currency {Currency}).", email, address, network, settings.CurrencyCode);
            return Ok(ToResponse(settings, address, network, signer.Value.SeedAddress, signer.Value.Slot));
        }

        /// <summary>
        /// Derives (without signing anything) the address at <paramref name="slot"/> for the seed
        /// identified by <paramref name="seedAddress"/> (see <c>GET /wallet/seeds</c>), for every
        /// currently-supported chain family in one call - there is no per-chain derive endpoint per family
        /// anymore (an Algorand-family address is genesis-independent, and an Ethereum-family address is the
        /// same across every EVM chain - see <c>CLAUDE.md</c>'s "EVM (Ethereum-family) support" note).
        /// </summary>
        /// <param name="seedAddress">The seed's own identifying (slot-0) address.</param>
        /// <param name="slot">ARC-76 derivation index within that seed. Defaults to <c>0</c>.</param>
        /// <response code="200">The derived addresses.</response>
        /// <response code="400">No seed with that address exists in the caller's vault.</response>
        /// <response code="401">The bearer token is missing, invalid, or expired, or no cached provider access token is available.</response>
        [AllowAnonymous]
        [RequiresBearerToken]
        [HttpGet("address/{seedAddress}/{slot:int?}")]
        [ProducesResponseType(typeof(DerivedAddressResponse), StatusCodes.Status200OK)]
        public async Task<IActionResult> GetAddress(string seedAddress, int? slot)
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
                    token => _accountRepository.DeriveAddressAsync(email, provider, seedAddress, resolvedSlot, token));

                // A slot-0 AVM address is already its own seed's identifying address (recognized for free,
                // without ever touching the activation registry - see ResolveSignerAsync). Only a non-zero
                // slot needs an explicit entry so it becomes resolvable by address alone later (e.g. from
                // POST /wallet/{network}/{address}/sign).
                if (resolvedSlot != 0)
                {
                    await ExecuteWithProviderTokenRefreshAsync(principal, email, provider, accessToken,
                        token => _addressActivationService.ActivateAsync(email, provider, token, address, nameof(ChainFamily.Avm), seedAddress, resolvedSlot));
                }

                var evmAddress = await ExecuteWithProviderTokenRefreshAsync(principal, email, provider, accessToken,
                    token => _accountRepository.DeriveEvmAddressAsync(email, provider, seedAddress, resolvedSlot, token));

                // Unlike Algorand slot 0, an EVM address is never a seed's own identifying address - it
                // always needs an activation-registry entry to be resolvable by address alone later.
                await ExecuteWithProviderTokenRefreshAsync(principal, email, provider, accessToken,
                    token => _addressActivationService.ActivateAsync(email, provider, token, evmAddress, nameof(ChainFamily.Evm), seedAddress, resolvedSlot));

                return Ok(new DerivedAddressResponse { Address = address, EvmAddress = evmAddress, SeedAddress = seedAddress, Slot = resolvedSlot });
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
        /// Lists every address currently resolvable to a signing seed/slot - the same resolution
        /// <see cref="SignTransactionGroup"/>/<see cref="GetAddressInfo"/> use, just for every known address
        /// at once rather than one at a time. Combines every seed's own slot-0 AVM address (active
        /// implicitly, never needing a derive/activate call) with every entry in the address activation
        /// registry (any non-zero AVM slot, every EVM address, and any externally-rekeyed AVM address - see
        /// <see cref="ActivateAddress"/>).
        /// </summary>
        /// <response code="200">The caller's active addresses.</response>
        /// <response code="401">The bearer token is missing, invalid, or expired, or no cached provider access token is available.</response>
        [AllowAnonymous]
        [RequiresBearerToken]
        [HttpGet("active-addresses")]
        [ProducesResponseType(typeof(ListActiveAddressesResponse), StatusCodes.Status200OK)]
        public async Task<IActionResult> GetActiveAddresses()
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
                var activated = await ExecuteWithProviderTokenRefreshAsync(principal, email, provider, accessToken,
                    token => _addressActivationService.ListAsync(email, provider, token));

                var addresses = seeds
                    .Select(s => new ActiveAddressResponse
                    {
                        Address = s.SeedAddress,
                        Family = nameof(ChainFamily.Avm),
                        SeedAddress = s.SeedAddress,
                        Slot = 0,
                        ActivatedUtc = s.CreatedUtc
                    })
                    .Concat(activated.Select(a => new ActiveAddressResponse
                    {
                        Address = a.Address,
                        Family = a.Family,
                        SeedAddress = a.SeedAddress,
                        Slot = a.Slot,
                        ActivatedUtc = a.ActivatedUtc
                    }))
                    .ToList();

                return Ok(new ListActiveAddressesResponse { Addresses = addresses });
            }
            catch (UnauthorizedAccessException ex)
            {
                return StatusCode(StatusCodes.Status401Unauthorized, new ProblemDetails { Title = "storage_access_denied", Detail = ex.Message });
            }
        }

        /// <summary>
        /// Reports whether Biatec currently knows which key signs for <paramref name="address"/> - a seed's
        /// own primary address, a previously-derived address (any slot), or one explicitly activated via
        /// <see cref="ActivateAddress"/>.
        /// </summary>
        /// <param name="network">Which chain <paramref name="address"/> belongs to - see <c>GET /chains</c>.</param>
        /// <param name="address">The address to look up.</param>
        /// <response code="200">The address's activation status.</response>
        /// <response code="400"><paramref name="network"/> is unrecognized.</response>
        /// <response code="401">The bearer token is missing, invalid, or expired, or no cached provider access token is available.</response>
        [AllowAnonymous]
        [RequiresBearerToken]
        [HttpGet("{network}/{address}/info")]
        [ProducesResponseType(typeof(AddressInfoResponse), StatusCodes.Status200OK)]
        public async Task<IActionResult> GetAddressInfo(string network, string address)
        {
            var authError = TryAuthenticate(requiredClaim: null, out var principal);
            if (authError != null)
            {
                return authError;
            }

            var resolvedNetwork = await _networkResolver.ResolveAsync(network);
            if (resolvedNetwork == null)
            {
                return BadRequest(new ProblemDetails { Title = "unknown_network", Detail = $"Unknown network '{network}'." });
            }

            var email = principal!.FindFirstValue(ClaimTypes.Email)!;
            var provider = principal.FindFirstValue(AuthSchemeNames.IdpClaimType) ?? string.Empty;
            var accessToken = ResolveProviderAccessToken(principal, email);

            try
            {
                var signer = await ResolveSignerAsync(principal, email, provider, accessToken, address);
                return Ok(new AddressInfoResponse
                {
                    Address = address,
                    Network = network,
                    Family = resolvedNetwork.Family.ToString(),
                    IsActive = signer != null,
                    SeedAddress = signer?.SeedAddress,
                    Slot = signer?.Slot ?? 0
                });
            }
            catch (UnauthorizedAccessException ex)
            {
                return StatusCode(StatusCodes.Status401Unauthorized, new ProblemDetails { Title = "storage_access_denied", Detail = ex.Message });
            }
        }

        /// <summary>
        /// Registers that <paramref name="seedAddress"/>/<paramref name="slot"/>'s key signs for
        /// <c>request.Address</c> - the entry point for AVM rekey support: rekey an external account to one
        /// of this account's addresses (see <c>GET /wallet/address/{seedAddress}/{slot}</c> for candidates,
        /// or mint a fresh one via <c>POST /wallet/seeds</c>), confirm the rekey transaction on-chain, then
        /// call this so <c>POST /wallet/{network}/{address}/sign</c> recognizes that address going forward.
        /// For a native address (one that's exactly the derived address for that seed/slot already), this
        /// just registers it immediately - the same thing <c>GET /wallet/address/{seedAddress}/{slot}</c>
        /// already does automatically, so calling this for a native address is rarely necessary.
        /// </summary>
        /// <param name="network">Which chain the address belongs to - see <c>GET /chains</c>. EVM chains are only accepted for a native address (EVM has no rekey concept).</param>
        /// <param name="seedAddress">Which seed's key signs for the address being activated - its own identifying (Algorand slot-0) address.</param>
        /// <param name="slot">ARC-76 derivation index within that seed.</param>
        /// <param name="request">The address to activate.</param>
        /// <response code="200">Activated.</response>
        /// <response code="400"><paramref name="network"/> is unrecognized, <paramref name="seedAddress"/> names an unknown seed, or an EVM address doesn't match its seed's own derived address.</response>
        /// <response code="401">The bearer token is missing, invalid, or expired, or no cached provider access token is available.</response>
        /// <response code="403">The token lacks the <c>sign</c> claim.</response>
        /// <response code="409">
        /// <c>request.Address</c> differs from the seed's derived address (an external/rekey scenario)
        /// but an on-chain check found it isn't currently rekeyed to that derived address - submit and
        /// confirm the on-chain rekey transaction first.
        /// </response>
        [AllowAnonymous]
        [RequiresBearerToken]
        [HttpPost("{network}/{seedAddress}/{slot:int}/activate")]
        [ProducesResponseType(typeof(AddressInfoResponse), StatusCodes.Status200OK)]
        public async Task<IActionResult> ActivateAddress(string network, string seedAddress, int slot, [FromBody] ActivateAddressRequest request)
        {
            var authError = TryAuthenticate(WalletScopes.Sign, out var principal);
            if (authError != null)
            {
                return authError;
            }

            var resolvedNetwork = await _networkResolver.ResolveAsync(network);
            if (resolvedNetwork == null)
            {
                return BadRequest(new ProblemDetails { Title = "unknown_network", Detail = $"Unknown network '{network}'." });
            }

            if (string.IsNullOrWhiteSpace(request.Address))
            {
                return BadRequest(new ProblemDetails { Title = "invalid_request", Detail = "Address is required." });
            }

            var address = request.Address;
            var email = principal!.FindFirstValue(ClaimTypes.Email)!;
            var provider = principal.FindFirstValue(AuthSchemeNames.IdpClaimType) ?? string.Empty;
            var accessToken = ResolveProviderAccessToken(principal, email);

            try
            {
                string derivedAddress;
                if (resolvedNetwork.Family == ChainFamily.Avm)
                {
                    derivedAddress = await ExecuteWithProviderTokenRefreshAsync(principal, email, provider, accessToken,
                        token => _accountRepository.DeriveAddressAsync(email, provider, seedAddress, slot, token));
                }
                else
                {
                    derivedAddress = await ExecuteWithProviderTokenRefreshAsync(principal, email, provider, accessToken,
                        token => _accountRepository.DeriveEvmAddressAsync(email, provider, seedAddress, slot, token));
                }

                if (!string.Equals(derivedAddress, address, StringComparison.Ordinal))
                {
                    if (resolvedNetwork.Family == ChainFamily.Evm)
                    {
                        return BadRequest(new ProblemDetails
                        {
                            Title = "invalid_request",
                            Detail = "An EVM address cannot be repointed to a different key - EVM has no rekey concept. Activate the seed's own EVM address instead (GET /wallet/address/{seedAddress}/{slot})."
                        });
                    }

                    bool verified;
                    try
                    {
                        verified = await IsRekeyedOnChainAsync(resolvedNetwork.AvmChain!, address, derivedAddress);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Unable to check {Address}'s on-chain rekey status on {Network}.", address, network);
                        return StatusCode(StatusCodes.Status503ServiceUnavailable, new ProblemDetails { Title = "algod_unavailable", Detail = "Unable to verify the on-chain rekey status right now. Please try again." });
                    }

                    if (!verified)
                    {
                        return StatusCode(StatusCodes.Status409Conflict, new ProblemDetails
                        {
                            Title = "rekey_not_confirmed",
                            Detail = $"'{address}' is not currently rekeyed to '{derivedAddress}' on-chain. Submit and confirm the rekey transaction first, then activate again."
                        });
                    }
                }

                await ExecuteWithProviderTokenRefreshAsync(principal, email, provider, accessToken,
                    token => _addressActivationService.ActivateAsync(email, provider, token, address, resolvedNetwork.Family.ToString(), seedAddress, slot));

                return Ok(new AddressInfoResponse
                {
                    Address = address,
                    Network = network,
                    Family = resolvedNetwork.Family.ToString(),
                    IsActive = true,
                    SeedAddress = seedAddress,
                    Slot = slot
                });
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
        [ProducesResponseType(typeof(SupportedCurrenciesResponse), StatusCodes.Status200OK)]
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
        [ProducesResponseType(typeof(ListSeedsResponse), StatusCodes.Status200OK)]
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
                    Seeds = seeds.Select(s => new SeedResponse { Address = s.SeedAddress, CreatedUtc = s.CreatedUtc, IsPrimary = s.IsPrimary }).ToList()
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
        [ProducesResponseType(typeof(SeedResponse), StatusCodes.Status200OK)]
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
                return Ok(new SeedResponse { Address = seed.SeedAddress, CreatedUtc = seed.CreatedUtc, IsPrimary = seed.IsPrimary });
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

        private static SpendingLimitResponse ToResponse(SpendingLimitSettings settings, string? address, string? network, string? seedAddress, int slot) => new()
        {
            CurrencyCode = settings.CurrencyCode,
            DailyLimit = settings.DailyLimit,
            WeeklyLimit = settings.WeeklyLimit,
            MonthlyLimit = settings.MonthlyLimit,
            Address = address,
            Network = network,
            SeedAddress = seedAddress,
            Slot = slot
        };

        /// <summary>
        /// Resolves <paramref name="address"/> to the <c>(seedAddress, slot)</c> that signs for it - a
        /// seed's own Algorand primary address (checked first, for free, no activation-registry read needed),
        /// else whatever <see cref="IAddressActivationService"/> has on file (populated automatically by
        /// <see cref="GetAddress"/>, or explicitly by
        /// <see cref="ActivateAddress"/>). Returns <c>null</c> if <paramref name="address"/> is neither.
        /// </summary>
        private async Task<(string SeedAddress, int Slot)?> ResolveSignerAsync(ClaimsPrincipal principal, string email, string provider, string? accessToken, string address)
        {
            var seeds = await ExecuteWithProviderTokenRefreshAsync(principal, email, provider, accessToken,
                token => _accountRepository.ListSeedsAsync(email, provider, token));
            var nativeSeed = seeds.FirstOrDefault(s => string.Equals(s.SeedAddress, address, StringComparison.Ordinal));
            if (nativeSeed != null)
            {
                return (nativeSeed.SeedAddress, 0);
            }

            var entry = await ExecuteWithProviderTokenRefreshAsync(principal, email, provider, accessToken,
                token => _addressActivationService.TryResolveAsync(email, provider, token, address));
            return entry == null ? null : (entry.SeedAddress, entry.Slot);
        }

        /// <summary>
        /// Checks whether <paramref name="address"/> is currently rekeyed on-chain to
        /// <paramref name="expectedAuthAddress"/> - a null/unset <c>auth-addr</c> means the account isn't
        /// rekeyed at all (still signs with its own key), which is also a "not confirmed" result here.
        /// </summary>
        private static async Task<bool> IsRekeyedOnChainAsync(AlgorandChain chain, string address, string expectedAuthAddress)
        {
            using var httpClient = HttpClientConfigurator.ConfigureHttpClient(chain.AlgodApiAddress, chain.AlgodApiToken);
            var algodApi = new DefaultApi(httpClient);
            var account = await algodApi.AccountInformationAsync(address);
            return account.AuthAddr != null && string.Equals(account.AuthAddr.EncodeAsString(), expectedAuthAddress, StringComparison.Ordinal);
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
            public const string Rekey = "rekey";
        }
    }
}
