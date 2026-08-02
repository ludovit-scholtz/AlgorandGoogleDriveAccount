using System.ComponentModel;
using System.Security.Claims;
using Algorand;
using Algorand.Algod;
using BiatecMCP.BusinessLogic;
using BiatecMCP.Helper;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;

namespace BiatecMCP.MCP
{
    /// <summary>
    /// MCP tools exposed to AI assistants: read the caller's Algorand address and sign/broadcast transfers.
    /// This class never touches key material - every signing operation is a bearer-token-forwarding HTTP
    /// call to BiatecOIDC's <c>POST /wallet/sign</c>, which does the actual signing (and enforces the
    /// caller's spending limit and <c>rekey</c>-claim requirement) on the caller's behalf. See
    /// <c>CLAUDE.md</c>'s BiatecMCP architecture notes for the full request flow.
    /// </summary>
    [McpServerToolType]
    public class BiatecMCP
    {
        /// <summary>Wallet-scope claim name (see BiatecOIDC's <c>JwtIssuerService.WalletApiScopes</c>) required to sign a transaction group.</summary>
        private const string SignClaimType = "sign";

        private readonly IBiatecWalletClient _walletClient;
        private readonly IHttpContextAccessor _httpContextAccessor;
        private readonly IOptionsMonitor<Model.AlgodConfiguration> _algodConfig;
        private readonly ILogger<BiatecMCP> _logger;

        public BiatecMCP(
            IBiatecWalletClient walletClient,
            IHttpContextAccessor httpContextAccessor,
            IOptionsMonitor<Model.AlgodConfiguration> algodConfig,
            ILogger<BiatecMCP> logger)
        {
            _walletClient = walletClient;
            _httpContextAccessor = httpContextAccessor;
            _algodConfig = algodConfig;
            _logger = logger;
        }

        public class GetAccountAddressResponse
        {
            public string Address { get; set; } = string.Empty;
            public string Error { get; set; } = string.Empty;
        }

        [McpServerTool(Name = "getAlgorandAddress"), Description("Returns the Algorand address of the signed-in Biatec account. Use slot=1 for the 'second address', slot=2 for the 'third', etc.; use primaryAddress (see listAlgorandAddresses) to derive from a different seed entirely.")]
        public async Task<GetAccountAddressResponse> GetAccountAddress(
            [Description("ARC-76 derivation slot. 0 = the default/first address (matches the signed-in identity), 1 = the 'second address', etc.")] int slot = 0,
            [Description("A specific seed's identifying address (see listAlgorandAddresses) to derive from instead of the primary seed.")] string? primaryAddress = null)
        {
            try
            {
                var address = await ResolveAlgorandAddressAsync(slot: slot, primaryAddress: primaryAddress);
                if (string.IsNullOrWhiteSpace(address))
                {
                    return new GetAccountAddressResponse
                    {
                        Error = "This account has no Algorand address yet. Complete storage-provider consent by signing in through BiatecOIDC (https://oidc.biatec.io/authorize) first."
                    };
                }

                return new GetAccountAddressResponse { Address = address };
            }
            catch (UnauthorizedAccessException ex)
            {
                return new GetAccountAddressResponse { Error = ex.Message };
            }
            catch (WalletApiException ex)
            {
                return new GetAccountAddressResponse { Error = ex.Message };
            }
            catch (Exception ex)
            {
                return new GetAccountAddressResponse { Error = SanitizeForToolResponse(ex, nameof(GetAccountAddress)) };
            }
        }

        public class ListAlgorandAddressesResponse
        {
            public List<Model.AddressResponse> Addresses { get; set; } = new();
            public string Error { get; set; } = string.Empty;
        }

        [McpServerTool(Name = "listAlgorandAddresses"), Description("Lists every seed's identifying address in the signed-in Biatec account, and which one is primary. Use an address from here as transferAsset/optIn/getAlgorandAddress's primaryAddress parameter.")]
        public async Task<ListAlgorandAddressesResponse> ListAlgorandAddresses()
        {
            try
            {
                var bearerToken = GetBearerToken();
                var result = await _walletClient.ListAddressesAsync(bearerToken);
                return new ListAlgorandAddressesResponse { Addresses = result.Addresses };
            }
            catch (UnauthorizedAccessException ex)
            {
                return new ListAlgorandAddressesResponse { Error = ex.Message };
            }
            catch (WalletApiException ex)
            {
                return new ListAlgorandAddressesResponse { Error = ex.Message };
            }
            catch (Exception ex)
            {
                return new ListAlgorandAddressesResponse { Error = SanitizeForToolResponse(ex, nameof(ListAlgorandAddresses)) };
            }
        }

        public class TransferAssetResponse
        {
            public string TxId { get; set; } = string.Empty;
            public string Error { get; set; } = string.Empty;
            public string ErrorType { get; set; } = string.Empty;
            public string? ExplorerLink { get; set; }
        }

        [McpServerTool(Name = "transferAsset"), Description("Signs and broadcasts a native ALGO payment or ASA transfer from the signed-in Biatec account. An empty receiver performs a self-transfer. Signs with the primary seed's default address unless primaryAddress/slot are given.")]
        public async Task<TransferAssetResponse> TransferAsset(
            [Description("Receiver address. If empty, performs a self-transfer.")] string receiverAccount = "",
            [Description("ASA id to transfer. If 0, performs a native ALGO payment instead.")] ulong assetId = 0,
            [Description("Amount to transfer, in the asset's base units (microAlgos for native ALGO).")] ulong amount = 0,
            [Description("Note to attach to the transaction. Empty attaches no note.")] string note = "",
            [Description("Blockchain genesis id. mainnet-v1.0 for Algorand mainnet, testnet-v1.0 for Algorand testnet.")] string genesisId = "mainnet-v1.0",
            [Description("A specific seed's identifying address (see listAlgorandAddresses) to sign with instead of the primary seed.")] string? primaryAddress = null,
            [Description("ARC-76 derivation slot to sign with. Defaults to 0 (matches the signed-in identity).")] int slot = 0)
        {
            var authError = RequireSignClaim();
            if (authError != null)
            {
                return new TransferAssetResponse { Error = authError, ErrorType = "InsufficientScope" };
            }

            try
            {
                var bearerToken = GetBearerToken();
                var senderAddress = await ResolveAlgorandAddressAsync(bearerToken, slot, primaryAddress);
                if (string.IsNullOrWhiteSpace(senderAddress))
                {
                    return new TransferAssetResponse
                    {
                        Error = "This account has no Algorand address yet. Complete storage-provider consent by signing in through BiatecOIDC first.",
                        ErrorType = "NoAlgorandAddress"
                    };
                }

                var sender = new Address(senderAddress);
                var receiver = string.IsNullOrWhiteSpace(receiverAccount) ? sender : new Address(receiverAccount);

                var (apiAddress, apiToken, explorerBaseUrl) = GetAlgodSettings(genesisId);
                using var httpClient = HttpClientConfigurator.ConfigureHttpClient(apiAddress, apiToken);
                var algodApi = new DefaultApi(httpClient);
                var suggestedParams = await algodApi.TransactionParamsAsync();

                var unsignedTransaction = assetId == 0
                    ? AlgorandTransactionBuilder.BuildPayment(sender, receiver, amount, note, suggestedParams)
                    : AlgorandTransactionBuilder.BuildAssetTransfer(sender, receiver, assetId, amount, note, suggestedParams);

                var signResult = await _walletClient.SignAsync(bearerToken, new[] { unsignedTransaction }, primaryAddress, slot);
                return await SubmitSignedTransactionsAsync(signResult.SignedTransactions, algodApi, explorerBaseUrl);
            }
            catch (WalletApiException ex)
            {
                return new TransferAssetResponse { Error = ex.Message, ErrorType = ex.ErrorCode };
            }
            catch (Algorand.ApiException<Algorand.Algod.Model.ErrorResponse> ex)
            {
                // Legitimate, already user-facing Algorand node error text (e.g. "insufficient balance") -
                // distinct from raw .NET exception text, same distinction BiatecOIDC's own error handling
                // draws.
                return new TransferAssetResponse { Error = ex.Result.Message, ErrorType = ex.GetType().ToString() };
            }
            catch (UnauthorizedAccessException ex)
            {
                return new TransferAssetResponse { Error = ex.Message, ErrorType = "Unauthorized" };
            }
            catch (Exception ex)
            {
                return new TransferAssetResponse { Error = SanitizeForToolResponse(ex, nameof(TransferAsset)), ErrorType = ex.GetType().ToString() };
            }
        }

        [McpServerTool(Name = "optIn"), Description("Opts the signed-in Biatec account in to an ASA (a zero-amount self-transfer, the standard Algorand opt-in pattern). Signs with the primary seed's default address unless primaryAddress/slot are given.")]
        public async Task<TransferAssetResponse> OptIn(
            [Description("ASA id to opt in to. Must be a positive number for an asset that exists.")] ulong assetId = 0,
            [Description("Note to attach to the transaction. Empty attaches no note.")] string note = "",
            [Description("Blockchain genesis id. mainnet-v1.0 for Algorand mainnet, testnet-v1.0 for Algorand testnet.")] string genesisId = "mainnet-v1.0",
            [Description("A specific seed's identifying address (see listAlgorandAddresses) to sign with instead of the primary seed.")] string? primaryAddress = null,
            [Description("ARC-76 derivation slot to sign with. Defaults to 0 (matches the signed-in identity).")] int slot = 0)
        {
            var authError = RequireSignClaim();
            if (authError != null)
            {
                return new TransferAssetResponse { Error = authError, ErrorType = "InsufficientScope" };
            }

            if (assetId == 0)
            {
                return new TransferAssetResponse { Error = "Asset id must be a positive number for an asset that exists.", ErrorType = "InvalidRequest" };
            }

            try
            {
                var bearerToken = GetBearerToken();
                var senderAddress = await ResolveAlgorandAddressAsync(bearerToken, slot, primaryAddress);
                if (string.IsNullOrWhiteSpace(senderAddress))
                {
                    return new TransferAssetResponse
                    {
                        Error = "This account has no Algorand address yet. Complete storage-provider consent by signing in through BiatecOIDC first.",
                        ErrorType = "NoAlgorandAddress"
                    };
                }

                var sender = new Address(senderAddress);
                var (apiAddress, apiToken, explorerBaseUrl) = GetAlgodSettings(genesisId);
                using var httpClient = HttpClientConfigurator.ConfigureHttpClient(apiAddress, apiToken);
                var algodApi = new DefaultApi(httpClient);
                var suggestedParams = await algodApi.TransactionParamsAsync();

                var unsignedTransaction = AlgorandTransactionBuilder.BuildOptIn(sender, assetId, note, suggestedParams);
                var signResult = await _walletClient.SignAsync(bearerToken, new[] { unsignedTransaction }, primaryAddress, slot);
                return await SubmitSignedTransactionsAsync(signResult.SignedTransactions, algodApi, explorerBaseUrl);
            }
            catch (WalletApiException ex)
            {
                return new TransferAssetResponse { Error = ex.Message, ErrorType = ex.ErrorCode };
            }
            catch (Algorand.ApiException<Algorand.Algod.Model.ErrorResponse> ex)
            {
                return new TransferAssetResponse { Error = ex.Result.Message, ErrorType = ex.GetType().ToString() };
            }
            catch (UnauthorizedAccessException ex)
            {
                return new TransferAssetResponse { Error = ex.Message, ErrorType = "Unauthorized" };
            }
            catch (Exception ex)
            {
                return new TransferAssetResponse { Error = SanitizeForToolResponse(ex, nameof(OptIn)), ErrorType = ex.GetType().ToString() };
            }
        }

        [McpServerTool(Name = "executeAlgorandTransaction"), Description("Broadcasts one or more already-signed Algorand transactions (base64 msgpack, e.g. from a prior sign step) to the network. Gated on the same 'sign' scope as transferAsset/optIn, which call this internally.")]
        public async Task<TransferAssetResponse> ExecuteTransaction(
            [Description("Signed transaction(s), base64-encoded msgpack, in submission order.")] List<string> signedTransactions,
            [Description("Blockchain genesis id. mainnet-v1.0 for Algorand mainnet, testnet-v1.0 for Algorand testnet.")] string genesisId = "mainnet-v1.0")
        {
            var authError = RequireSignClaim();
            if (authError != null)
            {
                return new TransferAssetResponse { Error = authError, ErrorType = "InsufficientScope" };
            }

            if (signedTransactions == null || signedTransactions.Count == 0)
            {
                return new TransferAssetResponse { Error = "At least one signed transaction is required.", ErrorType = "InvalidRequest" };
            }

            try
            {
                var (apiAddress, apiToken, explorerBaseUrl) = GetAlgodSettings(genesisId);
                using var httpClient = HttpClientConfigurator.ConfigureHttpClient(apiAddress, apiToken);
                var algodApi = new DefaultApi(httpClient);

                return await SubmitSignedTransactionsAsync(signedTransactions, algodApi, explorerBaseUrl);
            }
            catch (Algorand.ApiException<Algorand.Algod.Model.ErrorResponse> ex)
            {
                return new TransferAssetResponse { Error = ex.Result.Message, ErrorType = ex.GetType().ToString() };
            }
            catch (Exception ex)
            {
                return new TransferAssetResponse { Error = SanitizeForToolResponse(ex, nameof(ExecuteTransaction)), ErrorType = ex.GetType().ToString() };
            }
        }

        /// <summary>
        /// Submits one or more already-signed transactions (base64 msgpack) to Algod, in order - shared by
        /// <see cref="TransferAsset"/>/<see cref="OptIn"/> (which sign exactly one, then call this) and
        /// <see cref="ExecuteTransaction"/> (which broadcasts caller-supplied signed transactions directly).
        /// The current Algorand4 SDK only exposes a single-transaction submit call, so a multi-transaction
        /// group is submitted sequentially rather than as one atomic HTTP request; the returned <c>TxId</c>
        /// is the last transaction submitted.
        /// </summary>
        private static async Task<TransferAssetResponse> SubmitSignedTransactionsAsync(IReadOnlyList<string> signedTransactionsBase64, DefaultApi algodApi, string explorerBaseUrl)
        {
            Algorand.Algod.Model.PostTransactionsResponse postResult = null!;
            foreach (var signedBase64 in signedTransactionsBase64)
            {
                var signedTransaction = Algorand.Algod.Model.Transactions.SignedTransaction.FromBase64String(signedBase64);
                postResult = await Algorand.Utils.Utils.SubmitTransaction(algodApi, signedTransaction);
            }

            return new TransferAssetResponse { TxId = postResult.Txid, ExplorerLink = $"{explorerBaseUrl}{postResult.Txid}" };
        }

        private (string apiAddress, string apiToken, string explorerBaseUrl) GetAlgodSettings(string genesisId)
        {
            var algodConfig = _algodConfig.CurrentValue;

            if (algodConfig.Networks.TryGetValue(genesisId.ToLowerInvariant(), out var networkSettings))
            {
                return (networkSettings.ApiAddress, networkSettings.ApiToken, networkSettings.ExplorerBaseUrl);
            }

            throw new ArgumentException($"Unknown or unconfigured genesisId '{genesisId}'.");
        }

        /// <summary>
        /// The bearer token the AI client authenticated this MCP request with - the same token this tool
        /// forwards, unchanged, to BiatecOIDC's wallet API (see the class remarks). Thrown as
        /// <see cref="UnauthorizedAccessException"/> (never happens in practice once the MCP endpoint
        /// itself requires authentication - see <c>Program.cs</c> - but defensive here too) if somehow
        /// absent.
        /// </summary>
        private string GetBearerToken()
        {
            var header = _httpContextAccessor.HttpContext?.Request.Headers.Authorization.ToString();
            const string prefix = "Bearer ";
            if (string.IsNullOrEmpty(header) || !header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new UnauthorizedAccessException("Missing bearer token. Sign in through BiatecOIDC first.");
            }

            return header[prefix.Length..].Trim();
        }

        /// <summary>
        /// Resolves an Algorand address for signing/reporting. For the default identity (<paramref name="slot"/>
        /// <c>0</c>, no <paramref name="primaryAddress"/>), first tries the already-validated bearer
        /// token's own <c>algorand_address</c> claim (no extra network call - see BiatecOIDC's
        /// <c>JwtIssuerService.CreateAccessToken</c>), falling back to <c>GET /wallet/seeds</c> (the
        /// primary seed's address) if that claim is absent - e.g. because storage-provider consent was
        /// granted after this specific token was issued. For any non-default identity, resolves via
        /// BiatecOIDC's <c>GET /wallet/address/{primaryAddress}/{slot}</c> - <paramref name="primaryAddress"/>
        /// itself defaults to the vault's current primary seed (via <c>GET /wallet/seeds</c>) if not given,
        /// since only <paramref name="slot"/> was requested. Returns <c>null</c> (never throws) if no
        /// address is available at all.
        /// </summary>
        private async Task<string?> ResolveAlgorandAddressAsync(string? bearerToken = null, int slot = 0, string? primaryAddress = null)
        {
            if (slot == 0 && primaryAddress == null)
            {
                var claimAddress = _httpContextAccessor.HttpContext?.User?.FindFirstValue("algorand_address");
                if (!string.IsNullOrWhiteSpace(claimAddress))
                {
                    return claimAddress;
                }
            }

            var token = bearerToken ?? GetBearerToken();

            if (primaryAddress == null)
            {
                var seeds = await _walletClient.ListSeedsAsync(token);
                var primary = seeds.Seeds.FirstOrDefault(s => s.IsPrimary) ?? seeds.Seeds.FirstOrDefault();
                if (primary == null)
                {
                    return null;
                }

                if (slot == 0)
                {
                    return primary.Address;
                }

                primaryAddress = primary.Address;
            }

            var derived = await _walletClient.GetAddressAsync(token, primaryAddress, slot);
            return derived.Address;
        }

        /// <summary>
        /// Rejects a state-changing tool call outright (before any Drive/Algod/wallet-API work) if the
        /// caller's bearer token lacks the <c>sign</c> claim - defense in depth on top of
        /// <c>POST /wallet/sign</c>'s own identical check, so an insufficiently-scoped token is refused as
        /// cheaply as possible. Returns an error message to return to the caller, or <c>null</c> if the
        /// claim is present.
        /// </summary>
        private string? RequireSignClaim()
        {
            var hasSignClaim = string.Equals(
                _httpContextAccessor.HttpContext?.User?.FindFirstValue(SignClaimType),
                "true",
                StringComparison.Ordinal);

            return hasSignClaim
                ? null
                : "This action requires the 'sign' scope, which the current session's token does not have. Re-authenticate via BiatecOIDC requesting the 'sign' scope.";
        }

        /// <summary>
        /// Logs the full exception server-side and returns a generic, non-identifying message for the MCP
        /// tool response - never the raw <see cref="Exception.Message"/>, which could leak internal
        /// implementation detail to the connected AI client (a different trust boundary than this server's
        /// own logs). Legitimate, already user-facing error text (Algorand node errors, wallet API
        /// ProblemDetails) is returned directly elsewhere instead of routed through this method.
        /// </summary>
        private string SanitizeForToolResponse(Exception ex, string toolName)
        {
            _logger.LogError(ex, "Unexpected error in MCP tool {ToolName}.", toolName);
            return "An unexpected error occurred while processing the request. It has been logged server-side.";
        }
    }
}
