using System.ComponentModel;
using System.Globalization;
using System.Numerics;
using System.Security.Claims;
using System.Text.Json;
using Algorand;
using Algorand.Algod;
using BiatecMCP.BusinessLogic;
using BiatecMCP.Helper;
using BiatecMCP.Model;
using ModelContextProtocol.Server;

namespace BiatecMCP.MCP
{
    /// <summary>
    /// MCP tools exposed to AI assistants: read the caller's Algorand address(es), build unsigned
    /// transaction proposals, sign them via BiatecOIDC, and broadcast them - as three separate, chainable
    /// steps (<c>create*</c> → <c>signTransaction</c> → <c>executeAlgorandTransaction</c>) rather than one
    /// monolithic call, so an unsigned transaction can be inspected, handed to a different signer, or
    /// combined as part of a multisig proposal before it's ever broadcast. This class never touches key
    /// material - every signing operation is a bearer-token-forwarding HTTP call to BiatecOIDC's
    /// <c>POST /wallet/sign</c>, which does the actual signing (and enforces the caller's spending limit and
    /// <c>rekey</c>-claim requirement) on the caller's behalf. See <c>CLAUDE.md</c>'s BiatecMCP architecture
    /// notes for the full request flow.
    /// </summary>
    [McpServerToolType]
    public class BiatecMCP
    {
        /// <summary>Wallet-scope claim name (see BiatecOIDC's <c>JwtIssuerService.WalletApiScopes</c>) required to sign or broadcast a transaction.</summary>
        private const string SignClaimType = "sign";

        private const string NoAlgorandAddressMessage =
            "This account has no Algorand address yet. Complete storage-provider consent by signing in through BiatecOIDC first.";

        private const string NoEvmAddressMessage =
            "This account has no EVM address yet. Complete storage-provider consent by signing in through BiatecOIDC first.";

        private const string NoBitcoinAddressMessage =
            "This account has no Bitcoin-family address yet. Complete storage-provider consent by signing in through BiatecOIDC first.";

        private readonly IBiatecWalletClient _walletClient;
        private readonly IHttpContextAccessor _httpContextAccessor;
        private readonly DexSwapAggregatorService _dexSwapAggregator;
        private readonly IAramidBridgeConfigProvider _aramidBridgeConfigProvider;
        private readonly IAlgorandChainRegistry _chainRegistry;
        private readonly INetworkResolver _networkResolver;
        private readonly IPublicEvmRpcDataSource _evmRpcDataSource;
        private readonly IPublicBitcoinDataSource _bitcoinDataSource;
        private readonly ILogger<BiatecMCP> _logger;

        public BiatecMCP(
            IBiatecWalletClient walletClient,
            IHttpContextAccessor httpContextAccessor,
            DexSwapAggregatorService dexSwapAggregator,
            IAramidBridgeConfigProvider aramidBridgeConfigProvider,
            IAlgorandChainRegistry chainRegistry,
            INetworkResolver networkResolver,
            IPublicEvmRpcDataSource evmRpcDataSource,
            IPublicBitcoinDataSource bitcoinDataSource,
            ILogger<BiatecMCP> logger)
        {
            _walletClient = walletClient;
            _httpContextAccessor = httpContextAccessor;
            _dexSwapAggregator = dexSwapAggregator;
            _aramidBridgeConfigProvider = aramidBridgeConfigProvider;
            _chainRegistry = chainRegistry;
            _networkResolver = networkResolver;
            _evmRpcDataSource = evmRpcDataSource;
            _bitcoinDataSource = bitcoinDataSource;
            _logger = logger;
        }

        public class ListAlgorandAddressesResponse
        {
            public List<Model.SeedResponse> Addresses { get; set; } = new();
            public string Error { get; set; } = string.Empty;
        }

        [McpServerTool(Name = "listAlgorandAddresses"), Description("Lists every seed's identifying address in the signed-in Biatec account, and which one is primary. Use an address from here as any create*/getCryptoAddress tool's seedAddress parameter.")]
        public async Task<ListAlgorandAddressesResponse> ListAlgorandAddresses()
        {
            try
            {
                var bearerToken = GetBearerToken();
                var result = await _walletClient.ListSeedsAsync(bearerToken);
                return new ListAlgorandAddressesResponse { Addresses = result.Seeds };
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

        public class ListSupportedNetworksResponse
        {
            public List<NetworkSummary> Networks { get; set; } = new();
            public string Error { get; set; } = string.Empty;
        }

        [McpServerTool(Name = "listSupportedNetworks"),
         Description("Lists every exact 'network' code accepted by every tool that takes one (getCryptoAddress/getCryptoBalance/create*/signTransaction/executeTransaction) - every live Algorand-family (AVM) chain (e.g. 'algorand-mainnet', 'algorand-testnet', 'voi-mainnet', 'aramid-mainnet') plus every supported Ethereum-family (EVM) chain ('ethereum', 'gnosis', 'arbitrum', 'base') and Bitcoin-family chain ('bitcoin', 'bitcoin-cash'). ALWAYS call this first if you're unsure of the exact code - 'network' must match one of these Code values EXACTLY (case-insensitive, but no partial/fuzzy matching, no display names, no genesis ids, no numeric chain ids); passing anything else fails with an error listing every valid code. Never guess a network code, and never retry a failed call with a different network value - resolve the right one from this list first. No authentication required.")]
        public async Task<ListSupportedNetworksResponse> ListSupportedNetworks()
        {
            try
            {
                var networks = await _networkResolver.ListNetworksAsync();
                return new ListSupportedNetworksResponse { Networks = networks.ToList() };
            }
            catch (Exception ex)
            {
                return new ListSupportedNetworksResponse { Error = SanitizeForToolResponse(ex, nameof(ListSupportedNetworks)) };
            }
        }

        public class GetCryptoAddressResponse
        {
            public string Network { get; set; } = string.Empty;
            public string Family { get; set; } = string.Empty;
            public string Address { get; set; } = string.Empty;
            public string Error { get; set; } = string.Empty;
        }

        [McpServerTool(Name = "getCryptoAddress"),
         Description("Returns the signed-in Biatec account's address on the given blockchain network - Algorand-family (AVM), Ethereum-family (EVM), Bitcoin, and Bitcoin Cash are all supported, derived from the same seed. 'network' must be an exact code from listSupportedNetworks (e.g. 'algorand-mainnet', 'algorand-testnet', 'voi-mainnet', 'ethereum', 'arbitrum', 'base', 'bitcoin', 'bitcoin-cash') - call it first if unsure. An Algorand-family address is the same across every AVM chain; an Ethereum-family address is the same across every EVM chain - 'network' only picks which family's address to derive.")]
        public async Task<GetCryptoAddressResponse> GetCryptoAddress(
            [Description("Which network to derive the address for. Call listSupportedNetworks to see valid values.")] string network,
            [Description("ARC-76 derivation slot. 0 = the default/first address, 1 = the 'second address', etc.")] int slot = 0,
            [Description("A specific seed's identifying address (see listAlgorandAddresses) to derive from instead of the primary seed.")] string? seedAddress = null)
        {
            try
            {
                var resolved = await _networkResolver.ResolveAsync(network);
                if (resolved == null)
                {
                    return new GetCryptoAddressResponse { Network = network, Error = await BuildUnknownNetworkErrorAsync(network) };
                }

                if (resolved.Family == ChainFamily.Avm)
                {
                    var address = await ResolveAlgorandAddressAsync(slot: slot, seedAddress: seedAddress);
                    if (string.IsNullOrWhiteSpace(address))
                    {
                        return new GetCryptoAddressResponse { Network = resolved.DisplayName, Family = nameof(ChainFamily.Avm), Error = NoAlgorandAddressMessage };
                    }

                    return new GetCryptoAddressResponse { Network = resolved.DisplayName, Family = nameof(ChainFamily.Avm), Address = address };
                }

                if (resolved.Family is ChainFamily.Btc or ChainFamily.Bch)
                {
                    var bitcoinAddress = await ResolveBitcoinAddressAsync(resolved.Family, slot: slot, seedAddress: seedAddress);
                    if (string.IsNullOrWhiteSpace(bitcoinAddress))
                    {
                        return new GetCryptoAddressResponse { Network = resolved.DisplayName, Family = resolved.Family.ToString(), Error = NoBitcoinAddressMessage };
                    }

                    return new GetCryptoAddressResponse { Network = resolved.DisplayName, Family = resolved.Family.ToString(), Address = bitcoinAddress };
                }

                var evmAddress = await ResolveEvmAddressAsync(slot: slot, seedAddress: seedAddress);
                if (string.IsNullOrWhiteSpace(evmAddress))
                {
                    return new GetCryptoAddressResponse { Network = resolved.DisplayName, Family = nameof(ChainFamily.Evm), Error = NoEvmAddressMessage };
                }

                return new GetCryptoAddressResponse { Network = resolved.DisplayName, Family = nameof(ChainFamily.Evm), Address = evmAddress };
            }
            catch (UnauthorizedAccessException ex)
            {
                return new GetCryptoAddressResponse { Network = network, Error = ex.Message };
            }
            catch (WalletApiException ex)
            {
                return new GetCryptoAddressResponse { Network = network, Error = ex.Message };
            }
            catch (Exception ex)
            {
                return new GetCryptoAddressResponse { Network = network, Error = SanitizeForToolResponse(ex, nameof(GetCryptoAddress)) };
            }
        }

        public class AssetBalanceEntry
        {
            public ulong AssetId { get; set; }
            public ulong Amount { get; set; }
        }

        public class GetCryptoBalanceResponse
        {
            public string Network { get; set; } = string.Empty;
            public string Family { get; set; } = string.Empty;
            public string Address { get; set; } = string.Empty;
            public string NativeCurrencySymbol { get; set; } = string.Empty;
            public decimal NativeBalance { get; set; }

            /// <summary>Exact base-unit balance (µAlgo or wei) as a decimal string - a wei balance can exceed <c>ulong.MaxValue</c>, so this is never a numeric type.</summary>
            public string NativeBalanceBaseUnits { get; set; } = string.Empty;

            /// <summary>AVM only - the address's ASA holdings, capped at 50 (see <see cref="AssetsTruncated"/>). Always <c>null</c> for an EVM network.</summary>
            public List<AssetBalanceEntry>? Assets { get; set; }
            public bool AssetsTruncated { get; set; }
            public string Error { get; set; } = string.Empty;
            public string ErrorType { get; set; } = string.Empty;
        }

        private const int MaxAssetsInBalanceResponse = 50;

        [McpServerTool(Name = "getCryptoBalance"),
         Description("Returns the native-currency balance (and, on Algorand-family chains, ASA holdings) for an address on the given blockchain network - see listSupportedNetworks for valid 'network' values. Omit 'address' to check the signed-in account's own address on that network. EVM balances are the native gas token only (e.g. ETH) - ERC-20 token balances are not supported. Bitcoin/Bitcoin Cash balances are the confirmed on-chain balance from a public block explorer.")]
        public async Task<GetCryptoBalanceResponse> GetCryptoBalance(
            [Description("Which network to query. Call listSupportedNetworks to see valid values.")] string network,
            [Description("Address to check the balance of. Omit to check the signed-in account's own address on this network.")] string? address = null,
            [Description("A specific seed's identifying address to use when 'address' is omitted.")] string? seedAddress = null,
            [Description("ARC-76 derivation slot to use when 'address' is omitted. Defaults to 0.")] int slot = 0)
        {
            try
            {
                var resolved = await _networkResolver.ResolveAsync(network);
                if (resolved == null)
                {
                    return new GetCryptoBalanceResponse { Network = network, Error = await BuildUnknownNetworkErrorAsync(network) };
                }

                if (resolved.Family == ChainFamily.Avm)
                {
                    var resolvedAddress = address ?? await ResolveAlgorandAddressAsync(slot: slot, seedAddress: seedAddress);
                    if (string.IsNullOrWhiteSpace(resolvedAddress))
                    {
                        return new GetCryptoBalanceResponse { Network = resolved.DisplayName, Family = nameof(ChainFamily.Avm), Error = NoAlgorandAddressMessage };
                    }

                    using var httpClient = HttpClientConfigurator.ConfigureHttpClient(resolved.AvmChain!.AlgodApiAddress, resolved.AvmChain.AlgodApiToken);
                    var algodApi = new DefaultApi(httpClient);
                    var account = await algodApi.AccountInformationAsync(resolvedAddress);

                    var allAssets = account.Assets ?? new List<Algorand.Algod.Model.AssetHolding>();
                    var assets = allAssets.Take(MaxAssetsInBalanceResponse).Select(a => new AssetBalanceEntry { AssetId = a.AssetId, Amount = a.Amount }).ToList();

                    return new GetCryptoBalanceResponse
                    {
                        Network = resolved.DisplayName,
                        Family = nameof(ChainFamily.Avm),
                        Address = resolvedAddress,
                        NativeCurrencySymbol = "ALGO",
                        NativeBalance = account.Amount / 1_000_000m,
                        NativeBalanceBaseUnits = account.Amount.ToString(CultureInfo.InvariantCulture),
                        Assets = assets,
                        AssetsTruncated = allAssets.Count > MaxAssetsInBalanceResponse
                    };
                }

                if (resolved.Family is ChainFamily.Btc or ChainFamily.Bch)
                {
                    var bitcoinAddress = address ?? await ResolveBitcoinAddressAsync(resolved.Family, slot: slot, seedAddress: seedAddress);
                    if (string.IsNullOrWhiteSpace(bitcoinAddress))
                    {
                        return new GetCryptoBalanceResponse { Network = resolved.DisplayName, Family = resolved.Family.ToString(), Error = NoBitcoinAddressMessage };
                    }

                    var chainSlug = resolved.Family == ChainFamily.Btc ? BlockchairChainSlugs.Bitcoin : BlockchairChainSlugs.BitcoinCash;
                    var satoshis = await _bitcoinDataSource.TryGetBalanceAsync(chainSlug, bitcoinAddress);
                    if (satoshis == null)
                    {
                        return new GetCryptoBalanceResponse
                        {
                            Network = resolved.DisplayName,
                            Family = resolved.Family.ToString(),
                            Address = bitcoinAddress,
                            Error = "Could not reach the block explorer to check the balance right now. Try again shortly.",
                            ErrorType = "DataSourceUnavailable"
                        };
                    }

                    return new GetCryptoBalanceResponse
                    {
                        Network = resolved.DisplayName,
                        Family = resolved.Family.ToString(),
                        Address = bitcoinAddress,
                        NativeCurrencySymbol = resolved.Family == ChainFamily.Btc ? "BTC" : "BCH",
                        NativeBalance = satoshis.Value / 100_000_000m,
                        NativeBalanceBaseUnits = satoshis.Value.ToString(CultureInfo.InvariantCulture)
                    };
                }

                var evmAddress = address ?? await ResolveEvmAddressAsync(slot: slot, seedAddress: seedAddress);
                if (string.IsNullOrWhiteSpace(evmAddress))
                {
                    return new GetCryptoBalanceResponse { Network = resolved.DisplayName, Family = nameof(ChainFamily.Evm), Error = NoEvmAddressMessage };
                }

                var balance = await _evmRpcDataSource.TryGetBalanceAsync(resolved.EvmChain!.RpcUrl, evmAddress);
                if (balance == null)
                {
                    return new GetCryptoBalanceResponse
                    {
                        Network = resolved.DisplayName,
                        Family = nameof(ChainFamily.Evm),
                        Address = evmAddress,
                        Error = "Could not reach a live RPC node for this chain to check the balance right now. Try again shortly.",
                        ErrorType = "RpcUnavailable"
                    };
                }

                var divisor = BigInteger.Pow(10, resolved.EvmChain.NativeCurrencyDecimals);
                var humanBalance = (decimal)balance.Value / (decimal)divisor;

                return new GetCryptoBalanceResponse
                {
                    Network = resolved.DisplayName,
                    Family = nameof(ChainFamily.Evm),
                    Address = evmAddress,
                    NativeCurrencySymbol = resolved.EvmChain.NativeCurrencySymbol,
                    NativeBalance = humanBalance,
                    NativeBalanceBaseUnits = balance.Value.ToString(CultureInfo.InvariantCulture)
                };
            }
            catch (Algorand.ApiException<Algorand.Algod.Model.ErrorResponse> ex)
            {
                return new GetCryptoBalanceResponse { Network = network, Error = ex.Result.Message, ErrorType = ex.GetType().ToString() };
            }
            catch (UnauthorizedAccessException ex)
            {
                return new GetCryptoBalanceResponse { Network = network, Error = ex.Message };
            }
            catch (WalletApiException ex)
            {
                return new GetCryptoBalanceResponse { Network = network, Error = ex.Message };
            }
            catch (Exception ex)
            {
                return new GetCryptoBalanceResponse { Network = network, Error = SanitizeForToolResponse(ex, nameof(GetCryptoBalance)) };
            }
        }

        public class CreateTransactionResponse
        {
            public string UnsignedTransaction { get; set; } = string.Empty;
            public string Sender { get; set; } = string.Empty;
            public string? Receiver { get; set; }
            public ulong AssetId { get; set; }
            public ulong Amount { get; set; }
            public string Error { get; set; } = string.Empty;
            public string ErrorType { get; set; } = string.Empty;
        }

        [McpServerTool(Name = "createPaymentTransaction"),
         Description("Builds an unsigned native-ALGO payment or ASA transfer from the signed-in Biatec account - does NOT sign or broadcast it. Pass the result's UnsignedTransaction to signTransaction next, then the signed result to executeTransaction to broadcast it. An empty receiver builds a self-transfer.")]
        public async Task<CreateTransactionResponse> CreatePaymentTransaction(
            [Description("Receiver address. If empty, builds a self-transfer.")] string receiverAccount = "",
            [Description("ASA id to transfer. If 0, builds a native ALGO payment instead.")] ulong assetId = 0,
            [Description("Amount to transfer, in the asset's base units (microAlgos for native ALGO).")] ulong amount = 0,
            [Description("Note to attach to the transaction. Empty attaches no note.")] string note = "",
            [Description("Which network to build against - e.g. 'algorand-mainnet', 'algorand-testnet', 'voi-mainnet'. Call listSupportedNetworks to see the full list of valid, exact network codes.")] string network = "algorand-mainnet",
            [Description("A specific seed's identifying address (see listAlgorandAddresses) to build the transaction from instead of the primary seed.")] string? seedAddress = null,
            [Description("ARC-76 derivation slot to build the transaction from. Defaults to 0 (matches the signed-in identity).")] int slot = 0)
        {
            try
            {
                var senderAddress = await ResolveAlgorandAddressAsync(slot: slot, seedAddress: seedAddress);
                if (string.IsNullOrWhiteSpace(senderAddress))
                {
                    return new CreateTransactionResponse { Error = NoAlgorandAddressMessage, ErrorType = "NoAlgorandAddress" };
                }

                var sender = new Address(senderAddress);
                var receiver = string.IsNullOrWhiteSpace(receiverAccount) ? sender : new Address(receiverAccount);
                var suggestedParams = await GetSuggestedParamsAsync(network);

                var unsignedTransaction = assetId == 0
                    ? AlgorandTransactionBuilder.BuildPayment(sender, receiver, amount, note, suggestedParams)
                    : AlgorandTransactionBuilder.BuildAssetTransfer(sender, receiver, assetId, amount, note, suggestedParams);

                return new CreateTransactionResponse
                {
                    UnsignedTransaction = Convert.ToBase64String(unsignedTransaction),
                    Sender = senderAddress,
                    Receiver = receiver.EncodeAsString(),
                    AssetId = assetId,
                    Amount = amount
                };
            }
            catch (WalletApiException ex)
            {
                return new CreateTransactionResponse { Error = ex.Message, ErrorType = ex.ErrorCode };
            }
            catch (UnauthorizedAccessException ex)
            {
                return new CreateTransactionResponse { Error = ex.Message, ErrorType = "Unauthorized" };
            }
            catch (ArgumentException ex)
            {
                return new CreateTransactionResponse { Error = ex.Message, ErrorType = "InvalidRequest" };
            }
            catch (Exception ex)
            {
                return new CreateTransactionResponse { Error = SanitizeForToolResponse(ex, nameof(CreatePaymentTransaction)), ErrorType = ex.GetType().ToString() };
            }
        }

        public class CreateBitcoinTransactionResponse
        {
            public string UnsignedTransaction { get; set; } = string.Empty;
            public string Sender { get; set; } = string.Empty;
            public string Receiver { get; set; } = string.Empty;
            public long AmountSatoshis { get; set; }
            public long FeeSatoshis { get; set; }
            public long ChangeSatoshis { get; set; }
            public int InputCount { get; set; }
            public string Error { get; set; } = string.Empty;
            public string ErrorType { get; set; } = string.Empty;
        }

        [McpServerTool(Name = "createBitcoinTransaction"),
         Description("Builds an unsigned Bitcoin or Bitcoin Cash payment from the signed-in Biatec account - does NOT sign or broadcast it. Fetches spendable UTXOs and the current suggested fee rate from a public block explorer, greedily selects inputs largest-first, and computes change automatically (folded into the fee if it would be dust). Pass the result's UnsignedTransaction to signTransaction next (network='bitcoin' or 'bitcoin-cash', address=Sender), then the signed result to executeTransaction (same network) to broadcast it.")]
        public async Task<CreateBitcoinTransactionResponse> CreateBitcoinTransaction(
            [Description("Recipient address.")] string receiverAddress,
            [Description("Amount to send, in satoshis (1 BTC/BCH = 100,000,000 satoshis).")] long amountSatoshis,
            [Description("Which network to build for - 'bitcoin' or 'bitcoin-cash'.")] string network,
            [Description("A specific seed's identifying address to build the transaction from instead of the primary seed.")] string? seedAddress = null,
            [Description("ARC-76 derivation slot to build the transaction from. Defaults to 0 (matches the signed-in identity).")] int slot = 0)
        {
            try
            {
                var resolved = await _networkResolver.ResolveAsync(network);
                if (resolved == null)
                {
                    return new CreateBitcoinTransactionResponse { Error = await BuildUnknownNetworkErrorAsync(network), ErrorType = "InvalidRequest" };
                }

                if (resolved.Family is not (ChainFamily.Btc or ChainFamily.Bch))
                {
                    return new CreateBitcoinTransactionResponse { Error = $"'{network}' is not a Bitcoin-family network. Use 'bitcoin' or 'bitcoin-cash'.", ErrorType = "InvalidRequest" };
                }

                if (amountSatoshis <= 0)
                {
                    return new CreateBitcoinTransactionResponse { Error = "Amount must be positive.", ErrorType = "InvalidRequest" };
                }

                var senderAddress = await ResolveBitcoinAddressAsync(resolved.Family, slot: slot, seedAddress: seedAddress);
                if (string.IsNullOrWhiteSpace(senderAddress))
                {
                    return new CreateBitcoinTransactionResponse { Error = NoBitcoinAddressMessage, ErrorType = "NoBitcoinAddress" };
                }

                var chainSlug = resolved.Family == ChainFamily.Btc ? BlockchairChainSlugs.Bitcoin : BlockchairChainSlugs.BitcoinCash;
                var utxos = await _bitcoinDataSource.GetUtxosAsync(chainSlug, senderAddress);
                if (utxos.Count == 0)
                {
                    return new CreateBitcoinTransactionResponse { Error = $"'{senderAddress}' has no spendable UTXOs.", ErrorType = "NoUtxos" };
                }

                // 1 sat/byte is a conservative fallback if the explorer's own fee estimate is unavailable -
                // better to build a transaction that confirms slowly than to fail the whole request over a
                // fee-estimate outage.
                var feeRate = await _bitcoinDataSource.TryGetSuggestedFeeRateAsync(chainSlug) ?? 1m;

                BitcoinTransactionBuilder.BuildResult build;
                try
                {
                    build = BitcoinTransactionBuilder.Build(resolved.Family, utxos, senderAddress, receiverAddress, amountSatoshis, feeRate);
                }
                catch (InvalidOperationException ex)
                {
                    return new CreateBitcoinTransactionResponse { Error = ex.Message, ErrorType = "InsufficientFunds" };
                }

                var unsignedJson = JsonSerializer.SerializeToUtf8Bytes(build.Transaction);
                return new CreateBitcoinTransactionResponse
                {
                    UnsignedTransaction = Convert.ToBase64String(unsignedJson),
                    Sender = senderAddress,
                    Receiver = receiverAddress,
                    AmountSatoshis = amountSatoshis,
                    FeeSatoshis = build.FeeSatoshis,
                    ChangeSatoshis = build.ChangeSatoshis,
                    InputCount = build.InputCount
                };
            }
            catch (WalletApiException ex)
            {
                return new CreateBitcoinTransactionResponse { Error = ex.Message, ErrorType = ex.ErrorCode };
            }
            catch (UnauthorizedAccessException ex)
            {
                return new CreateBitcoinTransactionResponse { Error = ex.Message, ErrorType = "Unauthorized" };
            }
            catch (Exception ex)
            {
                return new CreateBitcoinTransactionResponse { Error = SanitizeForToolResponse(ex, nameof(CreateBitcoinTransaction)), ErrorType = ex.GetType().ToString() };
            }
        }

        [McpServerTool(Name = "createOptInTransaction"),
         Description("Builds an unsigned ASA opt-in (a zero-amount self-transfer, the standard Algorand pattern) for the signed-in Biatec account - does NOT sign or broadcast it. Pass the result's UnsignedTransaction to signTransaction next, then executeTransaction to broadcast it.")]
        public async Task<CreateTransactionResponse> CreateOptInTransaction(
            [Description("ASA id to opt in to. Must be a positive number for an asset that exists.")] ulong assetId = 0,
            [Description("Note to attach to the transaction. Empty attaches no note.")] string note = "",
            [Description("Which network to build against - e.g. 'algorand-mainnet', 'algorand-testnet', 'voi-mainnet'. Call listSupportedNetworks to see the full list of valid, exact network codes.")] string network = "algorand-mainnet",
            [Description("A specific seed's identifying address (see listAlgorandAddresses) to build the transaction from instead of the primary seed.")] string? seedAddress = null,
            [Description("ARC-76 derivation slot to build the transaction from. Defaults to 0 (matches the signed-in identity).")] int slot = 0)
        {
            if (assetId == 0)
            {
                return new CreateTransactionResponse { Error = "Asset id must be a positive number for an asset that exists.", ErrorType = "InvalidRequest" };
            }

            try
            {
                var senderAddress = await ResolveAlgorandAddressAsync(slot: slot, seedAddress: seedAddress);
                if (string.IsNullOrWhiteSpace(senderAddress))
                {
                    return new CreateTransactionResponse { Error = NoAlgorandAddressMessage, ErrorType = "NoAlgorandAddress" };
                }

                var sender = new Address(senderAddress);
                var suggestedParams = await GetSuggestedParamsAsync(network);
                var unsignedTransaction = AlgorandTransactionBuilder.BuildOptIn(sender, assetId, note, suggestedParams);

                return new CreateTransactionResponse
                {
                    UnsignedTransaction = Convert.ToBase64String(unsignedTransaction),
                    Sender = senderAddress,
                    Receiver = senderAddress,
                    AssetId = assetId,
                    Amount = 0
                };
            }
            catch (WalletApiException ex)
            {
                return new CreateTransactionResponse { Error = ex.Message, ErrorType = ex.ErrorCode };
            }
            catch (UnauthorizedAccessException ex)
            {
                return new CreateTransactionResponse { Error = ex.Message, ErrorType = "Unauthorized" };
            }
            catch (ArgumentException ex)
            {
                return new CreateTransactionResponse { Error = ex.Message, ErrorType = "InvalidRequest" };
            }
            catch (Exception ex)
            {
                return new CreateTransactionResponse { Error = SanitizeForToolResponse(ex, nameof(CreateOptInTransaction)), ErrorType = ex.GetType().ToString() };
            }
        }

        [McpServerTool(Name = "createAssetCreateTransaction"),
         Description("Builds an unsigned Algorand Standard Asset (ASA) creation transaction from the signed-in Biatec account - does NOT sign or broadcast it. Pass the result's UnsignedTransaction to signTransaction next, then executeTransaction to broadcast it. manager/reserve/freeze/clawback each default to the creator's own address if not given.")]
        public async Task<CreateTransactionResponse> CreateAssetCreateTransaction(
            [Description("Total units of the asset that will ever exist, in its own base units.")] ulong total,
            [Description("Number of decimal places the asset's base unit is divided into.")] ulong decimals,
            [Description("Short ticker/unit name (max 8 characters).")] string unitName,
            [Description("Full asset name (max 32 characters).")] string assetName,
            [Description("Optional URL with more information about the asset.")] string? url = null,
            [Description("Whether newly-opted-in holders start frozen (requires the freeze address to unfreeze them).")] bool defaultFrozen = false,
            [Description("Which network to build against - e.g. 'algorand-mainnet', 'algorand-testnet', 'voi-mainnet'. Call listSupportedNetworks to see the full list of valid, exact network codes.")] string network = "algorand-mainnet",
            [Description("A specific seed's identifying address (see listAlgorandAddresses) to create the asset from instead of the primary seed.")] string? seedAddress = null,
            [Description("ARC-76 derivation slot to create the asset from. Defaults to 0 (matches the signed-in identity).")] int slot = 0,
            [Description("Manager address. Defaults to the creator's own address.")] string? manager = null,
            [Description("Reserve address. Defaults to the creator's own address.")] string? reserve = null,
            [Description("Freeze address. Defaults to the creator's own address.")] string? freeze = null,
            [Description("Clawback address. Defaults to the creator's own address.")] string? clawback = null)
        {
            try
            {
                var senderAddress = await ResolveAlgorandAddressAsync(slot: slot, seedAddress: seedAddress);
                if (string.IsNullOrWhiteSpace(senderAddress))
                {
                    return new CreateTransactionResponse { Error = NoAlgorandAddressMessage, ErrorType = "NoAlgorandAddress" };
                }

                var sender = new Address(senderAddress);
                var suggestedParams = await GetSuggestedParamsAsync(network);
                var unsignedTransaction = AlgorandTransactionBuilder.BuildAssetCreate(
                    sender, total, decimals, unitName, assetName, url, defaultFrozen, suggestedParams,
                    manager: string.IsNullOrWhiteSpace(manager) ? null : new Address(manager),
                    reserve: string.IsNullOrWhiteSpace(reserve) ? null : new Address(reserve),
                    freeze: string.IsNullOrWhiteSpace(freeze) ? null : new Address(freeze),
                    clawback: string.IsNullOrWhiteSpace(clawback) ? null : new Address(clawback));

                return new CreateTransactionResponse
                {
                    UnsignedTransaction = Convert.ToBase64String(unsignedTransaction),
                    Sender = senderAddress
                };
            }
            catch (WalletApiException ex)
            {
                return new CreateTransactionResponse { Error = ex.Message, ErrorType = ex.ErrorCode };
            }
            catch (UnauthorizedAccessException ex)
            {
                return new CreateTransactionResponse { Error = ex.Message, ErrorType = "Unauthorized" };
            }
            catch (ArgumentException ex)
            {
                return new CreateTransactionResponse { Error = ex.Message, ErrorType = "InvalidRequest" };
            }
            catch (Exception ex)
            {
                return new CreateTransactionResponse { Error = SanitizeForToolResponse(ex, nameof(CreateAssetCreateTransaction)), ErrorType = ex.GetType().ToString() };
            }
        }

        public class DexQuoteResponse
        {
            public string ProviderName { get; set; } = string.Empty;
            public long OutputAmount { get; set; }
        }

        public class CreateSwapTransactionResponse
        {
            public List<DexQuoteResponse> Quotes { get; set; } = new();
            public string? BestProvider { get; set; }
            public List<string> UnsignedTransactions { get; set; } = new();
            public string Error { get; set; } = string.Empty;
            public string ErrorType { get; set; } = string.Empty;
        }

        [McpServerTool(Name = "createSwapTransaction"),
         Description("Quotes a swap across Biatec Router, Folks Router, and Haystack Router and reports the best price. Only builds a real unsigned transaction for Biatec Router's own route today - if a competing aggregator quotes better, the comparison is still returned but no transaction is attached for it yet (transaction building for the others isn't wired up). Pass any returned UnsignedTransactions to signTransaction next, then executeTransaction to broadcast.")]
        public async Task<CreateSwapTransactionResponse> CreateSwapTransaction(
            [Description("Asset id to swap from. 0 = native ALGO.")] long fromAssetId,
            [Description("Asset id to swap to. 0 = native ALGO.")] long toAssetId,
            [Description("Amount to swap, in fromAssetId's base units.")] long amount,
            [Description("Minimum acceptable output amount, in toAssetId's base units. 0 = no minimum.")] long receiveMinimum = 0,
            [Description("Which network to build against - e.g. 'algorand-mainnet', 'algorand-testnet', 'voi-mainnet'. Call listSupportedNetworks to see the full list of valid, exact network codes.")] string network = "algorand-mainnet",
            [Description("A specific seed's identifying address to swap from instead of the primary seed.")] string? seedAddress = null,
            [Description("ARC-76 derivation slot to swap from. Defaults to 0 (matches the signed-in identity).")] int slot = 0)
        {
            var response = new CreateSwapTransactionResponse();

            try
            {
                var quotes = await _dexSwapAggregator.GetAllQuotesAsync(fromAssetId, toAssetId, amount);
                response.Quotes = quotes.Select(q => new DexQuoteResponse { ProviderName = q.ProviderName, OutputAmount = q.OutputAmount }).ToList();

                var best = DexSwapAggregatorService.PickBest(quotes);
                if (best == null)
                {
                    response.Error = "No aggregator returned a quote for this pair right now.";
                    response.ErrorType = "NoQuoteAvailable";
                    return response;
                }

                response.BestProvider = best.ProviderName;

                if (!string.Equals(best.ProviderName, BiatecRouterProviderName, StringComparison.Ordinal))
                {
                    response.Error = $"{best.ProviderName} quoted the best price, but building a transaction for it isn't wired up yet in this MCP server - only {BiatecRouterProviderName}'s route can be built into a transaction today. You can still execute {BiatecRouterProviderName}'s route, or swap manually elsewhere for the better price.";
                    response.ErrorType = "TransactionBuildingNotAvailable";
                    return response;
                }

                var senderAddress = await ResolveAlgorandAddressAsync(slot: slot, seedAddress: seedAddress);
                if (string.IsNullOrWhiteSpace(senderAddress))
                {
                    response.Error = NoAlgorandAddressMessage;
                    response.ErrorType = "NoAlgorandAddress";
                    return response;
                }

                var suggestedParams = await GetSuggestedParamsAsync(network);
                var biatecRouterProvider = (BiatecRouterQuoteProvider)_dexSwapAggregator.Providers.Single(p => p.ProviderName == BiatecRouterProviderName);
                var routerParams = BiatecRouterConnector.Extensions.ToRouterParams(suggestedParams);
                var unsignedTransactions = await biatecRouterProvider.BuildRouteTransactionsAsync(
                    senderAddress, fromAssetId, toAssetId, amount, receiveMinimum, routerParams);

                response.UnsignedTransactions = unsignedTransactions.ToList();
                return response;
            }
            catch (WalletApiException ex)
            {
                response.Error = ex.Message;
                response.ErrorType = ex.ErrorCode;
                return response;
            }
            catch (UnauthorizedAccessException ex)
            {
                response.Error = ex.Message;
                response.ErrorType = "Unauthorized";
                return response;
            }
            catch (ArgumentException ex)
            {
                response.Error = ex.Message;
                response.ErrorType = "InvalidRequest";
                return response;
            }
            catch (Exception ex)
            {
                response.Error = SanitizeForToolResponse(ex, nameof(CreateSwapTransaction));
                response.ErrorType = ex.GetType().ToString();
                return response;
            }
        }

        public class CreateBridgeTransactionResponse
        {
            public string UnsignedTransaction { get; set; } = string.Empty;
            public string BridgeDepositAddress { get; set; } = string.Empty;
            public ulong SourceAmount { get; set; }
            public ulong FeeAmount { get; set; }
            public ulong DestinationAmount { get; set; }

            /// <summary><c>true</c> if Biatec confirmed the bridge deposit address on the destination chain currently holds enough of the destination token; <c>false</c> if it couldn't be checked (see <see cref="Warning"/>). A request that was checked and found insufficient never reaches this response - see <c>InsufficientDestinationLiquidity</c>.</summary>
            public bool? LiquidityVerified { get; set; }
            public string? Warning { get; set; }
            public string Error { get; set; } = string.Empty;
            public string ErrorType { get; set; } = string.Empty;
        }

        private sealed class DestinationLiquidityCheckResult
        {
            public bool? Verified { get; set; }
            public string? Warning { get; set; }

            /// <summary>Set only when liquidity was actually checked and found insufficient - the caller must fail closed on this, never return a built transaction.</summary>
            public string? Error { get; set; }
        }

        /// <summary>Aramid chain id per source genesisId - see https://github.com/AramidFinance/docs. Only Algorand mainnet is supported today.</summary>
        private static readonly Dictionary<string, long> AramidSourceChainIdsByGenesisId = new()
        {
            ["mainnet-v1.0"] = 416001
        };

        /// <summary>The one source network code Aramid bridging supports today - see <see cref="CreateBridgeTransaction"/>.</summary>
        private const string AlgorandMainnetCode = "algorand-mainnet";

        [McpServerTool(Name = "createBridgeTransaction"),
         Description("Builds an unsigned Aramid Finance bridge transaction (a pay/axfer sent to Aramid's bridge deposit address, with a note field encoding the destination chain/address/amounts) - does NOT sign or broadcast it. Fetches Aramid's live bridge configuration first and validates the route and amount bounds against it. For Algorand-family destination chains with a currently-live public algod node, also verifies the bridge deposit address there actually holds enough of the destination token, and refuses to build the transaction (InsufficientDestinationLiquidity) if not; for other destinations the response's LiquidityVerified/Warning fields explain why it couldn't be checked - confirm independently in that case before broadcasting anything but a small amount. Consider calling getBridgeConfiguration first to see valid routes/chains. Pass the result's UnsignedTransaction to signTransaction next, then executeTransaction (network=\"algorand-mainnet\") to broadcast.")]
        public async Task<CreateBridgeTransactionResponse> CreateBridgeTransaction(
            [Description("ASA id to bridge from Algorand. 0 = native ALGO.")] ulong assetId,
            [Description("Total amount to bridge, in the source asset's base units, INCLUDING Aramid's bridge fee - the fee is deducted from this amount, never added on top.")] ulong amount,
            [Description("Aramid destination chain id, e.g. 416101 for Voi, 8453 for Base - see Aramid's published chain configuration.")] long destinationNetwork,
            [Description("Recipient address on the destination chain.")] string destinationAddress,
            [Description("Destination token identifier, as defined in Aramid's bridge configuration for this route.")] string destinationToken,
            [Description("Optional memo forwarded to the destination chain, max 50 characters, letters/numbers/whitespace/./,/-/_//,@,*,+,$,% only.")] string? note = null,
            [Description("Which network to bridge from. Only 'algorand-mainnet' is supported today.")] string network = AlgorandMainnetCode,
            [Description("A specific seed's identifying address to bridge from instead of the primary seed.")] string? seedAddress = null,
            [Description("ARC-76 derivation slot to bridge from. Defaults to 0.")] int slot = 0)
        {
            if (!string.Equals(AlgorandMainnetCode, network, StringComparison.OrdinalIgnoreCase) ||
                !AramidSourceChainIdsByGenesisId.TryGetValue("mainnet-v1.0", out var sourceChainId))
            {
                return new CreateBridgeTransactionResponse { Error = $"Aramid bridging is only supported from network '{AlgorandMainnetCode}' today (got '{network}').", ErrorType = "InvalidRequest" };
            }

            try
            {
                var senderAddress = await ResolveAlgorandAddressAsync(slot: slot, seedAddress: seedAddress);
                if (string.IsNullOrWhiteSpace(senderAddress))
                {
                    return new CreateBridgeTransactionResponse { Error = NoAlgorandAddressMessage, ErrorType = "NoAlgorandAddress" };
                }

                var config = await _aramidBridgeConfigProvider.GetConfigAsync();

                var destinationChainKey = destinationNetwork.ToString(CultureInfo.InvariantCulture);
                if (!config.Chains.TryGetValue(destinationChainKey, out var destinationChain))
                {
                    return new CreateBridgeTransactionResponse { Error = $"Aramid does not currently list a destination chain with id {destinationNetwork}.", ErrorType = "UnknownDestinationChain" };
                }

                var sourceChainKey = sourceChainId.ToString(CultureInfo.InvariantCulture);
                if (!config.Chains.TryGetValue(sourceChainKey, out var sourceChain))
                {
                    return new CreateBridgeTransactionResponse { Error = "Aramid does not currently list Algorand mainnet as a source chain.", ErrorType = "UnknownSourceChain" };
                }

                var sourceTokenKey = assetId.ToString(CultureInfo.InvariantCulture);
                if (!config.Chains2Tokens.TryGetValue(sourceChainKey, out var byDestinationChain) ||
                    !byDestinationChain.TryGetValue(destinationChainKey, out var bySourceToken) ||
                    !bySourceToken.TryGetValue(sourceTokenKey, out var byDestinationToken) ||
                    !byDestinationToken.TryGetValue(destinationToken, out var mapping) ||
                    mapping.FeeAlternatives.Count == 0)
                {
                    return new CreateBridgeTransactionResponse { Error = $"Aramid does not currently offer a route from asset {assetId} on Algorand to token '{destinationToken}' on chain {destinationNetwork}.", ErrorType = "RouteNotFound" };
                }

                var suggestedParams = await GetSuggestedParamsAsync(network);
                var feeAlternative = AramidBridgeCalculator.ResolveFeeAlternative(mapping.FeeAlternatives, suggestedParams.LastRound);

                var feeAmount = AramidBridgeCalculator.ComputeFeeAmount(amount, feeAlternative);
                if (feeAmount >= amount)
                {
                    return new CreateBridgeTransactionResponse { Error = "Aramid's bridge fee would consume the entire amount - increase the amount to bridge.", ErrorType = "AmountTooSmall" };
                }

                var sourceAmount = amount - feeAmount;
                if (sourceAmount < (ulong)feeAlternative.MinimumAmount || sourceAmount > (ulong)feeAlternative.MaximumAmount)
                {
                    return new CreateBridgeTransactionResponse
                    {
                        Error = $"After the bridge fee, {sourceAmount} base units is outside Aramid's allowed range for this route ({feeAlternative.MinimumAmount}-{feeAlternative.MaximumAmount}).",
                        ErrorType = "AmountOutOfRange"
                    };
                }

                var sourceDecimals = sourceChain.Tokens.TryGetValue(sourceTokenKey, out var sourceTokenInfo) ? sourceTokenInfo.Decimals ?? 6 : 6;
                var destinationDecimals = destinationChain.Tokens.TryGetValue(destinationToken, out var destinationTokenInfo) ? destinationTokenInfo.Decimals ?? sourceDecimals : sourceDecimals;
                var destinationAmount = AramidBridgeCalculator.ComputeDestinationAmount(sourceAmount, sourceDecimals, destinationDecimals);

                var liquidityCheck = await CheckDestinationLiquidityAsync(destinationChain, destinationNetwork, destinationToken, destinationAmount);
                if (liquidityCheck.Error != null)
                {
                    return new CreateBridgeTransactionResponse { Error = liquidityCheck.Error, ErrorType = "InsufficientDestinationLiquidity" };
                }

                string transferNote;
                try
                {
                    transferNote = AramidBridgeCalculator.BuildTransferNote(destinationNetwork, destinationAddress, destinationToken, feeAmount, sourceAmount, destinationAmount, note);
                }
                catch (ArgumentException ex)
                {
                    return new CreateBridgeTransactionResponse { Error = ex.Message, ErrorType = "InvalidRequest" };
                }

                var sender = new Address(senderAddress);
                var bridgeDepositAddress = new Address(sourceChain.Address);
                var unsignedTransaction = assetId == 0
                    ? AlgorandTransactionBuilder.BuildPayment(sender, bridgeDepositAddress, amount, transferNote, suggestedParams)
                    : AlgorandTransactionBuilder.BuildAssetTransfer(sender, bridgeDepositAddress, assetId, amount, transferNote, suggestedParams);

                return new CreateBridgeTransactionResponse
                {
                    UnsignedTransaction = Convert.ToBase64String(unsignedTransaction),
                    BridgeDepositAddress = sourceChain.Address,
                    SourceAmount = sourceAmount,
                    FeeAmount = feeAmount,
                    DestinationAmount = destinationAmount,
                    LiquidityVerified = liquidityCheck.Verified,
                    Warning = liquidityCheck.Warning
                };
            }
            catch (WalletApiException ex)
            {
                return new CreateBridgeTransactionResponse { Error = ex.Message, ErrorType = ex.ErrorCode };
            }
            catch (UnauthorizedAccessException ex)
            {
                return new CreateBridgeTransactionResponse { Error = ex.Message, ErrorType = "Unauthorized" };
            }
            catch (ArgumentException ex)
            {
                return new CreateBridgeTransactionResponse { Error = ex.Message, ErrorType = "InvalidRequest" };
            }
            catch (Exception ex)
            {
                return new CreateBridgeTransactionResponse { Error = SanitizeForToolResponse(ex, nameof(CreateBridgeTransaction)), ErrorType = ex.GetType().ToString() };
            }
        }

        /// <summary>
        /// Checks whether Aramid's bridge deposit address on the destination chain currently holds enough of
        /// the destination token to fulfill this transfer - only possible for Algorand-family
        /// (<c>destinationChain.Type == "algo"</c>) destination chains with a currently-live public algod node
        /// (<see cref="IAlgorandChainRegistry"/>); EVM/NEAR destinations can't be checked this way and fall
        /// back to an honest warning instead, same as an unreachable/unknown Algorand-family chain.
        /// </summary>
        private async Task<DestinationLiquidityCheckResult> CheckDestinationLiquidityAsync(BusinessLogic.AramidChainItem destinationChain, long destinationNetwork, string destinationToken, ulong destinationAmount)
        {
            if (!string.Equals(destinationChain.Type, "algo", StringComparison.OrdinalIgnoreCase))
            {
                return new DestinationLiquidityCheckResult
                {
                    Verified = false,
                    Warning = $"Destination-chain bridge liquidity was not verified - chain {destinationNetwork} is not an Algorand-family chain, so Biatec cannot check its balance directly. Confirm independently before broadcasting anything but a small amount."
                };
            }

            AlgorandChain? liveChain;
            try
            {
                liveChain = await _chainRegistry.TryGetChainByAramidIdAsync(destinationNetwork);
            }
            catch (Exception)
            {
                liveChain = null;
            }

            if (liveChain == null)
            {
                return new DestinationLiquidityCheckResult
                {
                    Verified = false,
                    Warning = $"Destination-chain bridge liquidity was not verified - no currently-live public algod node is known for chain {destinationNetwork}. Confirm independently before broadcasting anything but a small amount."
                };
            }

            if (!ulong.TryParse(destinationToken, out var destinationAssetId))
            {
                return new DestinationLiquidityCheckResult
                {
                    Verified = false,
                    Warning = $"Destination-chain bridge liquidity was not verified - destination token '{destinationToken}' is not a numeric asset id Biatec can look up directly. Confirm independently before broadcasting anything but a small amount."
                };
            }

            try
            {
                using var httpClient = HttpClientConfigurator.ConfigureHttpClient(liveChain.AlgodApiAddress, liveChain.AlgodApiToken);
                var algodApi = new DefaultApi(httpClient);
                var account = await algodApi.AccountInformationAsync(destinationChain.Address);

                var availableBalance = destinationAssetId == 0
                    ? account.Amount
                    : account.Assets?.FirstOrDefault(a => a.AssetId == destinationAssetId)?.Amount ?? 0;

                if (availableBalance < destinationAmount)
                {
                    return new DestinationLiquidityCheckResult
                    {
                        Error = $"Aramid's bridge deposit address on destination chain {destinationNetwork} only holds {availableBalance} base units of token '{destinationToken}' - not enough to fulfill a transfer of {destinationAmount}. This transfer would likely be stranded; reduce the amount or try again later."
                    };
                }

                return new DestinationLiquidityCheckResult { Verified = true };
            }
            catch (Exception)
            {
                return new DestinationLiquidityCheckResult
                {
                    Verified = false,
                    Warning = $"Destination-chain bridge liquidity was not verified - the live algod node for chain {destinationNetwork} could not be queried. Confirm independently before broadcasting anything but a small amount."
                };
            }
        }

        public class AramidChainSummary
        {
            public long ChainId { get; set; }
            public string Name { get; set; } = string.Empty;

            /// <summary><c>"algo"</c>, <c>"eth"</c>, or <c>"near"</c>.</summary>
            public string Type { get; set; } = string.Empty;

            /// <summary>The bridge's own deposit/multisig address on this chain - transfers are sent here, never directly to the recipient.</summary>
            public string Address { get; set; } = string.Empty;

            /// <summary>Decimals for each token id Aramid recognizes on this chain, where known.</summary>
            public Dictionary<string, int?> TokenDecimals { get; set; } = new();
        }

        public class AramidRouteSummary
        {
            public long DestinationChainId { get; set; }
            public string SourceToken { get; set; } = string.Empty;
            public string DestinationToken { get; set; } = string.Empty;

            /// <summary>
            /// This route's fee schedule "generations" as Aramid published them, each with the round range
            /// it's valid for (<c>null</c> on either side = unbounded) - not resolved to "the current one"
            /// here, since that needs a live Algod round and this tool is deliberately Algod-independent;
            /// createBridgeTransaction resolves the actually-active one at build time via the same data.
            /// </summary>
            public List<AramidFeeAlternative> FeeAlternatives { get; set; } = new();
        }

        public class GetBridgeConfigurationResponse
        {
            public List<AramidChainSummary> Chains { get; set; } = new();
            public List<AramidRouteSummary> RoutesFromAlgorandMainnet { get; set; } = new();
            public string Error { get; set; } = string.Empty;
            public string ErrorType { get; set; } = string.Empty;
        }

        [McpServerTool(Name = "getBridgeConfiguration"),
         Description("Fetches Aramid Finance's live bridge configuration (from IPFS, per Aramid's own integration guide) and returns every chain it knows about plus every route out of Algorand mainnet (source chain 416001) - amount bounds, fee schedule generations, and token decimals. Call this before createBridgeTransaction to confirm the destinationNetwork/assetId/destinationToken combination you're about to use is actually valid and to see the allowed amount range, instead of guessing and getting a RouteNotFound/AmountOutOfRange error. Fee alternatives are returned as published (not resolved to 'the current one', since that needs a live round) - createBridgeTransaction resolves the actually-active one at build time. No authentication required.")]
        public async Task<GetBridgeConfigurationResponse> GetBridgeConfiguration(
            [Description("Optional Aramid destination chain id to filter routes to, e.g. 416101 for Voi. Omit to list every route out of Algorand mainnet.")] long? destinationChainId = null)
        {
            try
            {
                var config = await _aramidBridgeConfigProvider.GetConfigAsync();

                var chains = config.Chains.Values
                    .Select(c => new AramidChainSummary
                    {
                        ChainId = c.ChainId,
                        Name = c.Name,
                        Type = c.Type,
                        Address = c.Address,
                        TokenDecimals = c.Tokens.ToDictionary(t => t.Key, t => t.Value.Decimals)
                    })
                    .OrderBy(c => c.ChainId)
                    .ToList();

                var routes = new List<AramidRouteSummary>();
                var sourceChainKey = AramidSourceChainIdsByGenesisId["mainnet-v1.0"].ToString(CultureInfo.InvariantCulture);
                if (config.Chains2Tokens.TryGetValue(sourceChainKey, out var byDestinationChain))
                {
                    foreach (var (destinationChainKey, bySourceToken) in byDestinationChain)
                    {
                        if (!long.TryParse(destinationChainKey, NumberStyles.Integer, CultureInfo.InvariantCulture, out var destChainId))
                        {
                            continue;
                        }

                        if (destinationChainId.HasValue && destChainId != destinationChainId.Value)
                        {
                            continue;
                        }

                        foreach (var (sourceToken, byDestinationToken) in bySourceToken)
                        {
                            foreach (var (destinationToken, mapping) in byDestinationToken)
                            {
                                if (mapping.FeeAlternatives.Count == 0)
                                {
                                    continue;
                                }

                                routes.Add(new AramidRouteSummary
                                {
                                    DestinationChainId = destChainId,
                                    SourceToken = sourceToken,
                                    DestinationToken = destinationToken,
                                    FeeAlternatives = mapping.FeeAlternatives
                                });
                            }
                        }
                    }
                }

                return new GetBridgeConfigurationResponse
                {
                    Chains = chains,
                    RoutesFromAlgorandMainnet = routes
                        .OrderBy(r => r.DestinationChainId)
                        .ThenBy(r => r.SourceToken, StringComparer.Ordinal)
                        .ThenBy(r => r.DestinationToken, StringComparer.Ordinal)
                        .ToList()
                };
            }
            catch (Exception ex)
            {
                return new GetBridgeConfigurationResponse { Error = SanitizeForToolResponse(ex, nameof(GetBridgeConfiguration)), ErrorType = ex.GetType().ToString() };
            }
        }

        public class CreateMultisigTransactionResponse
        {
            public string UnsignedTransactionEnvelope { get; set; } = string.Empty;
            public string MultisigAddress { get; set; } = string.Empty;
            public string Error { get; set; } = string.Empty;
            public string ErrorType { get; set; } = string.Empty;
        }

        [McpServerTool(Name = "createMultisigTransaction"),
         Description("Builds an unsigned multisig payment/ASA transfer proposal from a (version, threshold, participantAddresses) multisig account - does NOT sign it. Each participant independently signs the returned UnsignedTransactionEnvelope with their own signTransaction call (in their own wallet/MCP session, not necessarily this one), then the signed copies are combined with mergeMultisigTransactions before executeTransaction broadcasts the result.")]
        public async Task<CreateMultisigTransactionResponse> CreateMultisigTransaction(
            [Description("Multisig version - always 1 for every multisig account created on Algorand today.")] int version,
            [Description("How many of participantAddresses must sign before the transaction can be broadcast.")] int threshold,
            [Description("The multisig account's participants, identified by their own normal Algorand addresses.")] List<string> participantAddresses,
            [Description("Receiver address. If empty, builds a self-transfer.")] string receiverAccount = "",
            [Description("ASA id to transfer. If 0, builds a native ALGO payment instead.")] ulong assetId = 0,
            [Description("Amount to transfer, in the asset's base units.")] ulong amount = 0,
            [Description("Note to attach to the transaction. Empty attaches no note.")] string note = "",
            [Description("Which network to build against - e.g. 'algorand-mainnet', 'algorand-testnet', 'voi-mainnet'. Call listSupportedNetworks to see the full list of valid, exact network codes.")] string network = "algorand-mainnet")
        {
            if (participantAddresses == null || participantAddresses.Count == 0)
            {
                return new CreateMultisigTransactionResponse { Error = "At least one participant address is required.", ErrorType = "InvalidRequest" };
            }

            if (threshold < 1 || threshold > participantAddresses.Count)
            {
                return new CreateMultisigTransactionResponse { Error = "threshold must be between 1 and the number of participant addresses.", ErrorType = "InvalidRequest" };
            }

            try
            {
                var multisigAddress = MultisigTransactionBuilder.DeriveAddress(version, threshold, participantAddresses);
                var sender = new Address(multisigAddress);
                var receiver = string.IsNullOrWhiteSpace(receiverAccount) ? sender : new Address(receiverAccount);
                var suggestedParams = await GetSuggestedParamsAsync(network);

                var inner = assetId == 0
                    ? AlgorandTransactionBuilder.BuildPayment(sender, receiver, amount, note, suggestedParams)
                    : AlgorandTransactionBuilder.BuildAssetTransfer(sender, receiver, assetId, amount, note, suggestedParams);

                var envelope = MultisigTransactionBuilder.BuildEnvelope(inner, version, threshold, participantAddresses);

                return new CreateMultisigTransactionResponse { UnsignedTransactionEnvelope = envelope, MultisigAddress = multisigAddress };
            }
            catch (ArgumentException ex)
            {
                return new CreateMultisigTransactionResponse { Error = ex.Message, ErrorType = "InvalidRequest" };
            }
            catch (Exception ex)
            {
                return new CreateMultisigTransactionResponse { Error = SanitizeForToolResponse(ex, nameof(CreateMultisigTransaction)), ErrorType = ex.GetType().ToString() };
            }
        }

        public class SignTransactionResponse
        {
            public List<string> SignedTransactions { get; set; } = new();
            public string Error { get; set; } = string.Empty;
            public string ErrorType { get; set; } = string.Empty;
        }

        [McpServerTool(Name = "signTransaction"),
         Description("Signs one or more unsigned transactions via BiatecOIDC, as 'address' on 'network' - Algorand-family (AVM), Ethereum-family (EVM), Bitcoin, and Bitcoin Cash are all supported. For AVM, each transaction is from createPaymentTransaction/createOptInTransaction/createAssetCreateTransaction/createSwapTransaction, or a createMultisigTransaction envelope (base64-encoded msgpack). For EVM (no create* tool builds these yet), each transaction is base64-encoded UTF-8 JSON with fields chainId/nonce/to/value/data/gasLimit plus either gasPrice (legacy) or maxFeePerGas+maxPriorityFeePerGas (EIP-1559) - all numeric fields as decimal or 0x-prefixed hex strings. For Bitcoin/Bitcoin Cash, pass exactly one transaction - the UnsignedTransaction from createBitcoinTransaction. Requires the 'sign' scope. 'address' must be the same address the create* tool (or getCryptoAddress/listAlgorandAddresses) returned as the sender - use getAddressInfo/activateCryptoAddress first if it's not the signed-in account's own default address. Pass the result to executeTransaction (same 'network') to broadcast, or to mergeMultisigTransactions first if this was a multisig envelope. 'network' must be the exact same network code the transaction was built for - see listSupportedNetworks for the full list.")]
        public async Task<SignTransactionResponse> SignTransaction(
            [Description("Unsigned transaction(s) - base64-encoded msgpack for an AVM network, or base64-encoded UTF-8 JSON for an EVM one (see this tool's own description for the JSON shape).")] List<string> unsignedTransactions,
            [Description("Which network 'address' belongs to - the exact code from listSupportedNetworks (e.g. 'algorand-mainnet', 'bitcoin').")] string network,
            [Description("Which identity signs - the sender address returned by the create* tool that built these transactions.")] string address)
        {
            var authError = RequireSignClaim();
            if (authError != null)
            {
                return new SignTransactionResponse { Error = authError, ErrorType = "InsufficientScope" };
            }

            if (unsignedTransactions == null || unsignedTransactions.Count == 0)
            {
                return new SignTransactionResponse { Error = "At least one transaction is required.", ErrorType = "InvalidRequest" };
            }

            List<byte[]> rawTransactions;
            try
            {
                rawTransactions = unsignedTransactions.Select(Convert.FromBase64String).ToList();
            }
            catch (FormatException)
            {
                return new SignTransactionResponse { Error = "Each transaction must be base64-encoded.", ErrorType = "InvalidRequest" };
            }

            try
            {
                var bearerToken = GetBearerToken();

                var resolved = await _networkResolver.ResolveAsync(network);
                if (resolved == null)
                {
                    return new SignTransactionResponse { Error = await BuildUnknownNetworkErrorAsync(network), ErrorType = "InvalidRequest" };
                }

                var signResult = await _walletClient.SignAsync(bearerToken, network, address, rawTransactions);
                return new SignTransactionResponse { SignedTransactions = signResult.SignedTransactions };
            }
            catch (WalletApiException ex)
            {
                return new SignTransactionResponse { Error = ex.Message, ErrorType = ex.ErrorCode };
            }
            catch (UnauthorizedAccessException ex)
            {
                return new SignTransactionResponse { Error = ex.Message, ErrorType = "Unauthorized" };
            }
            catch (Exception ex)
            {
                return new SignTransactionResponse { Error = SanitizeForToolResponse(ex, nameof(SignTransaction)), ErrorType = ex.GetType().ToString() };
            }
        }

        public class GetAddressInfoResponse
        {
            public string Address { get; set; } = string.Empty;
            public string Network { get; set; } = string.Empty;
            public string Family { get; set; } = string.Empty;
            public bool IsActive { get; set; }
            public string? SeedAddress { get; set; }
            public int Slot { get; set; }
            public string Error { get; set; } = string.Empty;
            public string ErrorType { get; set; } = string.Empty;
        }

        [McpServerTool(Name = "getAddressInfo"),
         Description("Reports whether BiatecOIDC currently knows which key signs for 'address' on 'network' - a seed's own default address is always active; any other address (a different slot, or an externally rekeyed AVM address) needs activateCryptoAddress first. Call this before signTransaction if you're unsure whether an address is ready to sign with.")]
        public async Task<GetAddressInfoResponse> GetAddressInfo(
            [Description("Which network 'address' belongs to. Call listSupportedNetworks to see valid values.")] string network,
            [Description("The address to check.")] string address)
        {
            try
            {
                var bearerToken = GetBearerToken();
                var info = await _walletClient.GetAddressInfoAsync(bearerToken, network, address);
                return new GetAddressInfoResponse
                {
                    Address = info.Address,
                    Network = info.Network,
                    Family = info.Family,
                    IsActive = info.IsActive,
                    SeedAddress = info.SeedAddress,
                    Slot = info.Slot
                };
            }
            catch (WalletApiException ex)
            {
                return new GetAddressInfoResponse { Error = ex.Message, ErrorType = ex.ErrorCode };
            }
            catch (UnauthorizedAccessException ex)
            {
                return new GetAddressInfoResponse { Error = ex.Message, ErrorType = "Unauthorized" };
            }
            catch (Exception ex)
            {
                return new GetAddressInfoResponse { Error = SanitizeForToolResponse(ex, nameof(GetAddressInfo)), ErrorType = ex.GetType().ToString() };
            }
        }

        public class ActivateCryptoAddressResponse
        {
            public string Address { get; set; } = string.Empty;
            public string Network { get; set; } = string.Empty;
            public string Family { get; set; } = string.Empty;
            public bool IsActive { get; set; }
            public string Error { get; set; } = string.Empty;
            public string ErrorType { get; set; } = string.Empty;
        }

        [McpServerTool(Name = "activateCryptoAddress"),
         Description("Registers that a specific seed/slot's key signs for 'address' on 'network' - the entry point for AVM rekey support. Flow: rekey an external Algorand account to one of this account's addresses (see getCryptoAddress/listAlgorandAddresses for candidates, or mint a fresh seed first), submit and confirm that rekey transaction on-chain yourself, then call this so signTransaction recognizes 'address' going forward. For a native address (already exactly the derived address for that seed/slot), this just registers it immediately - getCryptoAddress already does this automatically for any slot, so calling this for a native address is rarely necessary. Call listActiveAddresses to see everything already registered. Requires the 'sign' scope.")]
        public async Task<ActivateCryptoAddressResponse> ActivateCryptoAddress(
            [Description("Which network 'address' belongs to. EVM networks are only accepted for a native address (EVM has no rekey concept).")] string network,
            [Description("Which seed's key signs for 'address' - its own identifying address (see listAlgorandAddresses).")] string seedAddress,
            [Description("ARC-76 derivation slot within that seed.")] int slot,
            [Description("The address to activate.")] string address)
        {
            var authError = RequireSignClaim();
            if (authError != null)
            {
                return new ActivateCryptoAddressResponse { Error = authError, ErrorType = "InsufficientScope" };
            }

            try
            {
                var bearerToken = GetBearerToken();
                var info = await _walletClient.ActivateAddressAsync(bearerToken, network, seedAddress, slot, address);
                return new ActivateCryptoAddressResponse
                {
                    Address = info.Address,
                    Network = info.Network,
                    Family = info.Family,
                    IsActive = info.IsActive
                };
            }
            catch (WalletApiException ex)
            {
                return new ActivateCryptoAddressResponse { Error = ex.Message, ErrorType = ex.ErrorCode };
            }
            catch (UnauthorizedAccessException ex)
            {
                return new ActivateCryptoAddressResponse { Error = ex.Message, ErrorType = "Unauthorized" };
            }
            catch (Exception ex)
            {
                return new ActivateCryptoAddressResponse { Error = SanitizeForToolResponse(ex, nameof(ActivateCryptoAddress)), ErrorType = ex.GetType().ToString() };
            }
        }

        public class ListActiveAddressesResponse
        {
            public List<Model.ActiveAddressResponse> Addresses { get; set; } = new();
            public string Error { get; set; } = string.Empty;
            public string ErrorType { get; set; } = string.Empty;
        }

        [McpServerTool(Name = "listActiveAddresses"),
         Description("Lists every address currently resolvable to a signing seed/slot - every seed's own slot-0 Algorand address (active implicitly) plus every explicitly-activated address (any non-zero AVM slot, every EVM address, and any externally-rekeyed AVM address registered via activateCryptoAddress). Use an address from here directly with signTransaction.")]
        public async Task<ListActiveAddressesResponse> ListActiveAddresses()
        {
            try
            {
                var bearerToken = GetBearerToken();
                var result = await _walletClient.ListActiveAddressesAsync(bearerToken);
                return new ListActiveAddressesResponse { Addresses = result.Addresses };
            }
            catch (UnauthorizedAccessException ex)
            {
                return new ListActiveAddressesResponse { Error = ex.Message, ErrorType = "Unauthorized" };
            }
            catch (WalletApiException ex)
            {
                return new ListActiveAddressesResponse { Error = ex.Message, ErrorType = ex.ErrorCode };
            }
            catch (Exception ex)
            {
                return new ListActiveAddressesResponse { Error = SanitizeForToolResponse(ex, nameof(ListActiveAddresses)), ErrorType = ex.GetType().ToString() };
            }
        }

        public class MergeMultisigTransactionsResponse
        {
            public string SignedTransaction { get; set; } = string.Empty;
            public string Error { get; set; } = string.Empty;
            public string ErrorType { get; set; } = string.Empty;
        }

        [McpServerTool(Name = "mergeMultisigTransactions"),
         Description("Combines independently-signed copies of the same createMultisigTransaction envelope (collected from each cosigner's own signTransaction call, possibly across different sessions) into one transaction. Once at least the configured threshold of signatures are present, pass the result to executeAlgorandTransaction to broadcast.")]
        public Task<MergeMultisigTransactionsResponse> MergeMultisigTransactions(
            [Description("Independently-signed copies of the same multisig envelope, base64-encoded msgpack.")] List<string> signedTransactions)
        {
            if (signedTransactions == null || signedTransactions.Count == 0)
            {
                return Task.FromResult(new MergeMultisigTransactionsResponse { Error = "At least one signed copy is required.", ErrorType = "InvalidRequest" });
            }

            try
            {
                var merged = MultisigTransactionBuilder.Merge(signedTransactions);
                return Task.FromResult(new MergeMultisigTransactionsResponse { SignedTransaction = merged });
            }
            catch (Exception ex)
            {
                return Task.FromResult(new MergeMultisigTransactionsResponse { Error = SanitizeForToolResponse(ex, nameof(MergeMultisigTransactions)), ErrorType = ex.GetType().ToString() });
            }
        }

        public class ExecuteTransactionResponse
        {
            public string TxId { get; set; } = string.Empty;
            public string Error { get; set; } = string.Empty;
            public string ErrorType { get; set; } = string.Empty;
            public string? ExplorerLink { get; set; }
        }

        [McpServerTool(Name = "executeTransaction"),
         Description("Broadcasts one or more already-signed transactions to the blockchain - the one broadcast tool for every chain family this server supports (Algorand-family/AVM, Bitcoin, Bitcoin Cash). 'network' (see listSupportedNetworks) must be the exact same network the transaction(s) were built and signed for - broadcasting to the wrong network always fails, since a transaction signed for one network's genesis/chain is invalid on any other. Requires the 'sign' scope.")]
        public async Task<ExecuteTransactionResponse> ExecuteTransaction(
            [Description("Signed transaction(s) to broadcast, base64-encoded, in submission order. AVM: one or more msgpack-encoded transactions (e.g. from signTransaction or mergeMultisigTransactions). Bitcoin/BitcoinCash: exactly one raw signed transaction.")] List<string> signedTransactions,
            [Description("Which network to broadcast on - the exact network code the transaction(s) were built/signed for, e.g. 'algorand-mainnet', 'algorand-testnet', 'voi-mainnet', 'bitcoin', 'bitcoin-cash'. Call listSupportedNetworks to see the full list of valid, exact network codes.")] string network)
        {
            var authError = RequireSignClaim();
            if (authError != null)
            {
                return new ExecuteTransactionResponse { Error = authError, ErrorType = "InsufficientScope" };
            }

            if (signedTransactions == null || signedTransactions.Count == 0)
            {
                return new ExecuteTransactionResponse { Error = "At least one signed transaction is required.", ErrorType = "InvalidRequest" };
            }

            try
            {
                var resolved = await _networkResolver.ResolveAsync(network);
                if (resolved == null)
                {
                    return new ExecuteTransactionResponse { Error = await BuildUnknownNetworkErrorAsync(network), ErrorType = "InvalidRequest" };
                }

                if (resolved.Family is ChainFamily.Btc or ChainFamily.Bch)
                {
                    return await ExecuteBitcoinTransactionAsync(resolved.Family, signedTransactions);
                }

                if (resolved.Family == ChainFamily.Evm)
                {
                    return new ExecuteTransactionResponse { Error = "Broadcasting EVM transactions isn't supported yet - submit the signed raw transaction yourself via that chain's own eth_sendRawTransaction RPC.", ErrorType = "InvalidRequest" };
                }

                var (apiAddress, apiToken, explorerBaseUrl) = await GetAlgodSettings(network);
                using var httpClient = HttpClientConfigurator.ConfigureHttpClient(apiAddress, apiToken);
                var algodApi = new DefaultApi(httpClient);

                return await SubmitSignedTransactionsAsync(signedTransactions, algodApi, explorerBaseUrl);
            }
            catch (Algorand.ApiException<Algorand.Algod.Model.ErrorResponse> ex)
            {
                return new ExecuteTransactionResponse { Error = ex.Result.Message, ErrorType = ex.GetType().ToString() };
            }
            catch (ArgumentException ex)
            {
                return new ExecuteTransactionResponse { Error = ex.Message, ErrorType = "InvalidRequest" };
            }
            catch (Exception ex)
            {
                return new ExecuteTransactionResponse { Error = SanitizeForToolResponse(ex, nameof(ExecuteTransaction)), ErrorType = ex.GetType().ToString() };
            }
        }

        private async Task<ExecuteTransactionResponse> ExecuteBitcoinTransactionAsync(ChainFamily family, List<string> signedTransactions)
        {
            if (signedTransactions.Count != 1)
            {
                return new ExecuteTransactionResponse { Error = "Exactly one signed transaction is required for Bitcoin/Bitcoin Cash.", ErrorType = "InvalidRequest" };
            }

            byte[] rawBytes;
            try
            {
                rawBytes = Convert.FromBase64String(signedTransactions[0]);
            }
            catch (FormatException)
            {
                return new ExecuteTransactionResponse { Error = "The signed transaction must be base64-encoded.", ErrorType = "InvalidRequest" };
            }

            var chainSlug = family == ChainFamily.Btc ? BlockchairChainSlugs.Bitcoin : BlockchairChainSlugs.BitcoinCash;
            var rawHex = Convert.ToHexStringLower(rawBytes);
            var txId = await _bitcoinDataSource.TryBroadcastAsync(chainSlug, rawHex);
            if (txId == null)
            {
                return new ExecuteTransactionResponse { Error = "The block explorer rejected the broadcast, or was unreachable. Try again shortly.", ErrorType = "BroadcastFailed" };
            }

            return new ExecuteTransactionResponse { TxId = txId, ExplorerLink = $"https://blockchair.com/{chainSlug}/transaction/{txId}" };
        }

        private const string BiatecRouterProviderName = "BiatecRouter";

        /// <summary>
        /// Submits one or more already-signed transactions (base64 msgpack) to Algod, in order. The current
        /// Algorand4 SDK only exposes a single-transaction submit call, so a multi-transaction group is
        /// submitted sequentially rather than as one atomic HTTP request; the returned <c>TxId</c> is the
        /// last transaction submitted.
        /// </summary>
        private static async Task<ExecuteTransactionResponse> SubmitSignedTransactionsAsync(IReadOnlyList<string> signedTransactionsBase64, DefaultApi algodApi, string explorerBaseUrl)
        {
            Algorand.Algod.Model.PostTransactionsResponse postResult = null!;
            foreach (var signedBase64 in signedTransactionsBase64)
            {
                var signedTransaction = Algorand.Algod.Model.Transactions.SignedTransaction.FromBase64String(signedBase64);
                postResult = await Algorand.Utils.Utils.SubmitTransaction(algodApi, signedTransaction);
            }

            return new ExecuteTransactionResponse
            {
                TxId = postResult.Txid,
                ExplorerLink = string.IsNullOrEmpty(explorerBaseUrl) ? null : $"{explorerBaseUrl}{postResult.Txid}"
            };
        }

        /// <summary>
        /// Explorer URLs for chains whose genesis id is known here. Different Algorand network *variants*
        /// need genuinely different explorer URLs - a mainnet explorer doesn't necessarily support testnet,
        /// and vice versa (Allo Explorer, a single blanket fallback this table replaced, is mainnet-only and
        /// silently returned a non-working link for testnet). A chain with no entry here (Voi, Aramid, ...)
        /// simply gets no explorer link rather than a guessed one that might not work.
        /// </summary>
        private static readonly Dictionary<string, string> KnownExplorerBaseUrls = new(StringComparer.OrdinalIgnoreCase)
        {
            ["mainnet-v1.0"] = "https://algorand.scan.biatec.io/transaction/",
            ["testnet-v1.0"] = "https://lora.algokit.io/testnet/transaction/"
        };

        /// <summary>
        /// Resolves algod connection details for <paramref name="network"/> - an exact network code (see
        /// <see cref="INetworkResolver"/>'s remarks), never a friendly/fuzzy name. The explorer link is
        /// resolved from <see cref="KnownExplorerBaseUrls"/> by the chain's actual genesis id; a
        /// locally-configured <c>Algod:Networks</c> entry's own <see cref="AlgodNetworkSettings.ExplorerBaseUrl"/>
        /// overrides that when explicitly set, so an operator can still point at a custom/self-hosted
        /// explorer per network. Throws <see cref="ArgumentException"/> (message lists every valid network
        /// code) for an unresolvable network, or one that resolves to a non-AVM chain family.
        /// </summary>
        internal async Task<(string apiAddress, string apiToken, string explorerBaseUrl)> GetAlgodSettings(string network)
        {
            var resolved = await _networkResolver.ResolveAsync(network);
            if (resolved is not { Family: ChainFamily.Avm, AvmChain: not null })
            {
                throw new ArgumentException(await BuildUnknownNetworkErrorAsync(network));
            }

            var explorerBaseUrl = resolved.ConfiguredExplorerBaseUrlOverride
                ?? KnownExplorerBaseUrls.GetValueOrDefault(resolved.AvmChain.GenesisId, string.Empty);
            return (resolved.AvmChain.AlgodApiAddress, resolved.AvmChain.AlgodApiToken, explorerBaseUrl);
        }

        /// <summary>
        /// Builds a clear "unknown network" error naming every currently-valid network code (see
        /// <see cref="INetworkResolver.ListNetworksAsync"/>), used consistently across every
        /// <c>create*</c>/<c>getCryptoAddress</c>/<c>getCryptoBalance</c>/<c>executeTransaction</c> tool so a
        /// connected agent always sees the same closed vocabulary and error shape regardless of which tool
        /// it called with a bad <c>network</c> value.
        /// </summary>
        private async Task<string> BuildUnknownNetworkErrorAsync(string network)
        {
            var codes = (await _networkResolver.ListNetworksAsync()).Select(n => n.Code);
            return $"Unknown network '{network}'. Supported network codes: {string.Join(", ", codes)}.";
        }

        /// <summary>Fetches Algod's current suggested transaction parameters for <paramref name="network"/> - used by every <c>create*</c> tool.</summary>
        private async Task<Algorand.Algod.Model.TransactionParametersResponse> GetSuggestedParamsAsync(string network)
        {
            var (apiAddress, apiToken, _) = await GetAlgodSettings(network);
            using var httpClient = HttpClientConfigurator.ConfigureHttpClient(apiAddress, apiToken);
            var algodApi = new DefaultApi(httpClient);
            return await algodApi.TransactionParamsAsync();
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
        /// Resolves an Algorand address for signing/reporting - a thin wrapper over
        /// <see cref="ResolveDerivedAddressAsync"/>. Returns <c>null</c> (never throws) if no address is
        /// available at all.
        /// </summary>
        private async Task<string?> ResolveAlgorandAddressAsync(string? bearerToken = null, int slot = 0, string? seedAddress = null)
        {
            var derived = await ResolveDerivedAddressAsync(bearerToken, slot, seedAddress);
            return derived?.Address;
        }

        /// <summary>
        /// Resolves an EVM address for reporting - a thin wrapper over
        /// <see cref="ResolveDerivedAddressAsync"/>, same live-derivation call as
        /// <see cref="ResolveAlgorandAddressAsync"/> (both chain families are derived together in one
        /// <c>GET /wallet/address/{seedAddress}/{slot}</c> call - see that method's remarks for why there is
        /// deliberately no separate per-chain-family claim shortcut). Returns <c>null</c> (never throws) if
        /// no address is available at all.
        /// </summary>
        private async Task<string?> ResolveEvmAddressAsync(string? bearerToken = null, int slot = 0, string? seedAddress = null)
        {
            var derived = await ResolveDerivedAddressAsync(bearerToken, slot, seedAddress);
            return derived?.EvmAddress;
        }

        /// <summary>
        /// Resolves a Bitcoin or Bitcoin Cash address for signing/reporting - same thin-wrapper shape as
        /// <see cref="ResolveAlgorandAddressAsync"/>/<see cref="ResolveEvmAddressAsync"/>, since both
        /// Bitcoin-family addresses are derived together with everything else in the one
        /// <see cref="ResolveDerivedAddressAsync"/> call.
        /// </summary>
        private async Task<string?> ResolveBitcoinAddressAsync(ChainFamily family, string? bearerToken = null, int slot = 0, string? seedAddress = null)
        {
            var derived = await ResolveDerivedAddressAsync(bearerToken, slot, seedAddress);
            return family == ChainFamily.Btc ? derived?.BitcoinAddress : derived?.BitcoinCashAddress;
        }

        /// <summary>
        /// Derives the Algorand *and* EVM address at <paramref name="slot"/> for the seed identified by
        /// <paramref name="seedAddress"/>, always via a live BiatecOIDC <c>GET /wallet/address/{seedAddress}/{slot}</c>
        /// call - the same call, resolved the same way, regardless of chain family. There is deliberately no
        /// per-chain-family claim-based shortcut here (BiatecOIDC used to stamp an <c>algorand_address</c>
        /// claim - and, briefly, a mirrored <c>evm_address</c> one - directly onto the token so the default
        /// identity's *address* could be read without a network call; that asymmetry, before the mirrored
        /// claim existed, was the root cause of a real reported bug where "what is my Algorand address"
        /// succeeded off the claim while "what is my Ethereum address" failed outright, since it had no
        /// claim shortcut and so always needed a live Drive-backed call that could fail if the cached
        /// provider token had gone stale). Both chain families now resolve identically: only the *seed
        /// selector* is ever read from a claim - the bearer token's own <c>primary_seed_address</c> claim
        /// (set by BiatecOIDC's <c>JwtIssuerService</c>) - and only when <paramref name="seedAddress"/> isn't
        /// given, falling back to <c>GET /wallet/seeds</c>'s primary seed if the claim is absent (e.g. a
        /// token issued before this claim existed, or storage-provider consent granted afterwards). Returns
        /// <c>null</c> (never throws) if no seed is available at all.
        /// </summary>
        private async Task<DerivedAddressResponse?> ResolveDerivedAddressAsync(string? bearerToken, int slot, string? seedAddress)
        {
            var token = bearerToken ?? GetBearerToken();

            var explicitSeedAddress = seedAddress;
            seedAddress ??= _httpContextAccessor.HttpContext?.User?.FindFirstValue("primary_seed_address");

            if (seedAddress != null)
            {
                try
                {
                    return await _walletClient.GetAddressAsync(token, seedAddress, slot);
                }
                catch (WalletApiException ex) when (ex.ErrorCode == "seed_not_found" && explicitSeedAddress == null)
                {
                    // The bearer token's own primary_seed_address claim is captured once at issuance and
                    // carried forward unchanged across refreshes (see JwtIssuerService's refresh_token
                    // handling) - a long-lived token can therefore end up caching a seed selector that no
                    // longer resolves (e.g. a refresh token minted before this account's vault existed in
                    // its current form). Self-heal by falling back to the account's live seed list instead
                    // of failing outright. A *caller*-supplied seedAddress is never second-guessed this way
                    // - the caller named that seed on purpose, so a real "no such seed" stays an error.
                }
            }

            var seeds = await _walletClient.ListSeedsAsync(token);
            var primary = seeds.Seeds.FirstOrDefault(s => s.IsPrimary) ?? seeds.Seeds.FirstOrDefault();
            if (primary == null)
            {
                return null;
            }

            return await _walletClient.GetAddressAsync(token, primary.Address, slot);
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
