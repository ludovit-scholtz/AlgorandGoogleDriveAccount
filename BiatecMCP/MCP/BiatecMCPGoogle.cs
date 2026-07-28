using Algorand;
using Algorand.Algod;
using BiatecMCP.BusinessLogic;
using BiatecMCP.Model;
using BiatecSelfCustodyCore.Model;
using BiatecSelfCustodyCore.Repository;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Drive.v3;
using Google.Apis.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.IO;
using System.Text.Json;

namespace BiatecMCP.MCP
{
    [McpServerToolType]
    public class BiatecMCPGoogle
    {
        private readonly IDistributedCache _cache;
        private readonly ICloudAccountRepository _cloudAccountRepository;
        private readonly IDevicePairingService _devicePairingService;
        private readonly IOptionsMonitor<BiatecSelfCustodyCore.Model.Configuration> _config;
        private readonly IOptionsMonitor<BiatecMCP.Model.AlgodConfiguration> _algodConfig;
        private readonly IOptionsMonitor<BiatecMCP.Model.McpTransferLimitsConfiguration> _transferLimits;

        public BiatecMCPGoogle(
            IDistributedCache cache,
            ICloudAccountRepository cloudAccountRepository,
            IDevicePairingService devicePairingService,
            IOptionsMonitor<BiatecSelfCustodyCore.Model.Configuration> config,
            IOptionsMonitor<BiatecMCP.Model.AlgodConfiguration> algodConfig,
            IOptionsMonitor<BiatecMCP.Model.McpTransferLimitsConfiguration> transferLimits
            )
        {
            _cache = cache;
            _cloudAccountRepository = cloudAccountRepository;
            _devicePairingService = devicePairingService;
            _config = config;
            _algodConfig = algodConfig;
            _transferLimits = transferLimits;
        }

        private (string apiAddress, string apiToken, string explorerBaseUrl) GetAlgodSettings(string genesisId)
        {
            var algodConfig = _algodConfig.CurrentValue;
            
            if (algodConfig.Networks.TryGetValue(genesisId.ToLowerInvariant(), out var networkSettings))
            {
                return (networkSettings.ApiAddress, networkSettings.ApiToken, networkSettings.ExplorerBaseUrl);
            }

            throw new Exception($"Unsupported genesis id: {genesisId}. Supported networks: {string.Join(", ", algodConfig.Networks.Keys)}");
        }

        public class GetAccountAddressResponse
        {
            public string Address { get; set; } = string.Empty;
            public string Error { get; set; } = string.Empty;
        }

        [McpServerTool(Name = "getAlgorandAddress"), Description("Loads the Algorand account address stored at the google store.")]
        public async Task<GetAccountAddressResponse> GetAccountAddress(McpServer mcpServer, [Description("You can use slot to identify the account. Default account is at slot 1. Second account can be slot 2, and so on.")] int slot = 1)
        {
            try
            {
                var sessionId = mcpServer.SessionId;
                if (string.IsNullOrEmpty(sessionId)) throw new Exception("Unable to determine the session id");

                var deviceInfo = await _devicePairingService.GetDeviceInfoInternalAsync(sessionId);
                if (string.IsNullOrEmpty(deviceInfo?.AccessToken))
                {
                    throw new Exception($"Initiate google access and pair your device by signing at {_config.CurrentValue.Host}/pair.html?session={sessionId}");
                }

                if (string.IsNullOrEmpty(deviceInfo.Email))
                {
                    throw new Exception($"Unable to determine the email from the access token. You can try login again at {_config.CurrentValue.Host}/pair.html?session={sessionId}");
                }

                var provider = StorageProviderExtensions.Parse(deviceInfo.Provider);
                var account = await _cloudAccountRepository.LoadAccountAsync(deviceInfo.Email, slot, provider, deviceInfo.AccessToken);
                if (account == null)
                {
                    throw new Exception($"Unable to load the Algorand account from google store. Make sure the claim to access the google store to create files and load created files is granted to biatec app and try to login again. You can try login again at {_config.CurrentValue.Host}/pair.html?session={sessionId}");
                }
                return new GetAccountAddressResponse { Address = account.Address.EncodeAsString() };
            }
            catch (UnauthorizedAccessException unauthorizedEx)
            {
                // Handle authorization exceptions from CloudAccountRepository
                return new GetAccountAddressResponse
                {
                    Error = $"Google access token has expired or is invalid. Please re-authenticate at {_config.CurrentValue.Host}/pair.html?session={mcpServer.SessionId}. Details: {unauthorizedEx.Message}"
                };
            }
            catch (Google.GoogleApiException googleEx) when (googleEx.HttpStatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                // Handle Google API unauthorized error specifically
                return new GetAccountAddressResponse
                {
                    Error = $"Google access token has expired or is invalid. Please re-authenticate at {_config.CurrentValue.Host}/pair.html?session={mcpServer.SessionId}"
                };
            }
            catch (Exception ex)
            {
                return new GetAccountAddressResponse { Error = ex.Message };
            }
        }

        public class TransferAssetResponse
        {
            public string TxId { get; set; } = string.Empty;
            public string Error { get; set; } = string.Empty;
            public string ErrorType { get; set; } = string.Empty;
            public string ExplorerLink { get; internal set; }
        }

        [McpServerTool(Name = "transferAsset"), Description("Allows the google store account transfer the assets.")]
        public async Task<TransferAssetResponse> TransferAsset(
            McpServer mcpServer,
            [Description("You can use slot to identify the account which will sign the transfer. Default account is at slot 1. Second account can be slot 2, and so on.")] int slot = 1,
            [Description("Receiver. If empty it will execute self signed transaction.")] string receiverAccount = "",
            [Description("ASA id to transfer. If asset id is 0, it will execute native token transaction.")] ulong assetId = 0,
            [Description("Amount to transfer")] ulong amount = 0,
            [Description("Note to attach to the transaction. If empty, it will not attach any note.")] string note = "",
            //[Description("Expiration duration in blocks. Valid until round is calculated as current block round plus validUntilDiff.")] ulong validUntilDiff = 1000,
            [Description("Blockchain genesis id. mainnet-v1.0 for algorand mainnet, testnet-v1.0 for algorand testnet")] string genesisId = "mainnet-v1.0"
            )
        {
            try
            {
                var sessionId = mcpServer.SessionId;
                if (string.IsNullOrEmpty(sessionId)) throw new Exception("Unable to determine the session id");

                var deviceInfo = await _devicePairingService.GetDeviceInfoInternalAsync(sessionId);
                if (string.IsNullOrEmpty(deviceInfo?.AccessToken))
                {
                    throw new Exception($"Initiate google access and pair your device by signing at {_config.CurrentValue.Host}/pair.html?session={sessionId}");
                }

                // Server-side spend ceiling / receiver allowlist (F-04) - checked before touching the
                // Drive/Algod/credential path so a disallowed transfer is rejected as cheaply as possible.
                var maxAmount = _transferLimits.CurrentValue.MaxAmount;
                if (Helper.TransferPolicy.ExceedsMaxAmount(amount, maxAmount))
                {
                    return new TransferAssetResponse
                    {
                        Error = $"Transfer amount {amount} exceeds the configured maximum of {maxAmount}.",
                        ErrorType = "TransferLimitExceeded"
                    };
                }

                if (!Helper.TransferPolicy.IsReceiverAllowed(receiverAccount, deviceInfo.AllowedReceivers))
                {
                    return new TransferAssetResponse
                    {
                        Error = $"Receiver {receiverAccount} is not on this session's allowed-receiver list.",
                        ErrorType = "ReceiverNotAllowed"
                    };
                }

                var (apiAddress, apiToken, explorerBaseUrl) = GetAlgodSettings(genesisId);

                var httpClient = HttpClientConfigurator.ConfigureHttpClient(apiAddress, apiToken);
                DefaultApi algodApiInstance = new DefaultApi(httpClient);

                if (string.IsNullOrEmpty(deviceInfo.Email))
                {
                    throw new Exception($"Unable to determine the email from the access token. You can try login again at {_config.CurrentValue.Host}/pair.html?session={sessionId}");
                }

                var provider = StorageProviderExtensions.Parse(deviceInfo.Provider);
                var account = await _cloudAccountRepository.LoadAccountAsync(deviceInfo.Email, slot, provider, deviceInfo.AccessToken);
                if (account == null)
                {
                    throw new Exception($"Unable to load the Algorand account from google store. Make sure the claim to access the google store to create files and load created files is granted to biatec app and try to login again. You can try login again at {_config.CurrentValue.Host}/pair.html?session={sessionId}");
                }

                if (assetId == 0)
                {
                    var result = await account.MakePaymentTo(new Algorand.Address(receiverAccount), amount, note, algodApiInstance);
                    return new TransferAssetResponse { TxId = result.Txid, ExplorerLink = $"{explorerBaseUrl}{result.Txid}" };
                }
                else
                {
                    var result = await account.MakeAssetTransferTo(new Algorand.Address(receiverAccount), amount, assetId, note, algodApiInstance);
                    return new TransferAssetResponse { TxId = result.Txid, ExplorerLink = $"{explorerBaseUrl}{result.Txid}" };
                }
            }
            catch (Algorand.ApiException<Algorand.Algod.Model.ErrorResponse> ex)
            {
                // Handle authorization exceptions from CloudAccountRepository
                return new TransferAssetResponse
                {
                    Error = ex.Result.Message,
                    ErrorType = ex.GetType().ToString()
                };
            }
            catch (UnauthorizedAccessException unauthorizedEx)
            {
                // Handle authorization exceptions from CloudAccountRepository
                return new TransferAssetResponse
                {
                    Error = $"Google access token has expired or is invalid. Please re-authenticate at {_config.CurrentValue.Host}/pair.html?session={mcpServer.SessionId}. Details: {unauthorizedEx.Message}",
                    ErrorType = unauthorizedEx.GetType().ToString()

                };
            }
            catch (Google.GoogleApiException googleEx) when (googleEx.HttpStatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                // Handle Google API unauthorized error specifically
                return new TransferAssetResponse
                {
                    Error = $"Google access token has expired or is invalid. Please re-authenticate at {_config.CurrentValue.Host}/pair.html?session={mcpServer.SessionId}",
                    ErrorType = googleEx.GetType().ToString()
                };
            }
            catch (Exception ex)
            {
                return new TransferAssetResponse
                {
                    Error = ex.Message,
                    ErrorType = ex.GetType().ToString()
                };
            }
        }
        [McpServerTool(Name = "optIn"), Description("Allows the google store account to opt in to an asset.")]
        public async Task<TransferAssetResponse> OptIn(
            McpServer mcpServer,
            [Description("You can use slot to identify the account which will sign the transfer. Default account is at slot 1. Second account can be slot 2, and so on.")] int slot = 1,
            [Description("ASA id to transfer. Asset id must be positive number and asset must exists.")] ulong assetId = 0,
            [Description("Note to attach to the transaction. If empty, it will not attach any note.")] string note = "",
            //[Description("Expiration duration in blocks. Valid until round is calculated as current block round plus validUntilDiff.")] ulong validUntilDiff = 1000,
            [Description("Blockchain genesis id. mainnet-v1.0 for algorand mainnet, testnet-v1.0 for algorand testnet")] string genesisId = "mainnet-v1.0"
            )
        {
            try
            {
                var sessionId = mcpServer.SessionId;
                if (string.IsNullOrEmpty(sessionId)) throw new Exception("Unable to determine the session id");

                var deviceInfo = await _devicePairingService.GetDeviceInfoInternalAsync(sessionId);
                if (string.IsNullOrEmpty(deviceInfo?.AccessToken))
                {
                    throw new Exception($"Initiate google access and pair your device by signing at {_config.CurrentValue.Host}/pair.html?session={sessionId}");
                }

                var (apiAddress, apiToken, explorerBaseUrl) = GetAlgodSettings(genesisId);

                var httpClient = HttpClientConfigurator.ConfigureHttpClient(apiAddress, apiToken);
                DefaultApi algodApiInstance = new DefaultApi(httpClient);

                if (string.IsNullOrEmpty(deviceInfo.Email))
                {
                    throw new Exception($"Unable to determine the email from the access token. You can try login again at {_config.CurrentValue.Host}/pair.html?session={sessionId}");
                }

                var provider = StorageProviderExtensions.Parse(deviceInfo.Provider);
                var account = await _cloudAccountRepository.LoadAccountAsync(deviceInfo.Email, slot, provider, deviceInfo.AccessToken);
                if (account == null)
                {
                    throw new Exception($"Unable to load the Algorand account from google store. Make sure the claim to access the google store to create files and load created files is granted to biatec app and try to login again. You can try login again at {_config.CurrentValue.Host}/pair.html?session={sessionId}");
                }
                if (assetId == 0)
                {
                    throw new Exception("Asset id must be positive number and asset must exists.");
                }
                else
                {
                    var result = await account.MakeAssetTransferTo(account.Address, 0, assetId, note, algodApiInstance);
                    return new TransferAssetResponse { TxId = result.Txid, ExplorerLink = $"{explorerBaseUrl}{result.Txid}" };
                }
            }
            catch (Algorand.ApiException<Algorand.Algod.Model.ErrorResponse> ex)
            {
                // Handle authorization exceptions from CloudAccountRepository
                return new TransferAssetResponse
                {
                    Error = ex.Result.Message,
                    ErrorType = ex.GetType().ToString()
                };
            }
            catch (UnauthorizedAccessException unauthorizedEx)
            {
                // Handle authorization exceptions from CloudAccountRepository
                return new TransferAssetResponse
                {
                    Error = $"Google access token has expired or is invalid. Please re-authenticate at {_config.CurrentValue.Host}/pair.html?session={mcpServer.SessionId}. Details: {unauthorizedEx.Message}",
                    ErrorType = unauthorizedEx.GetType().ToString()

                };
            }
            catch (Google.GoogleApiException googleEx) when (googleEx.HttpStatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                // Handle Google API unauthorized error specifically
                return new TransferAssetResponse
                {
                    Error = $"Google access token has expired or is invalid. Please re-authenticate at {_config.CurrentValue.Host}/pair.html?session={mcpServer.SessionId}",
                    ErrorType = googleEx.GetType().ToString()
                };
            }
            catch (Exception ex)
            {
                return new TransferAssetResponse
                {
                    Error = ex.Message,
                    ErrorType = ex.GetType().ToString()
                };
            }
        }
    }
}