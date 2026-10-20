using System.Text.Json;
using System.Text.Json.Serialization;
using BiatecMCP.Helper;
using BiatecMCP.Model;
using Microsoft.Extensions.Options;

namespace BiatecMCP.BusinessLogic
{
    /// <inheritdoc cref="ISolanaRpcDataSource"/>
    /// <remarks>
    /// Raw JSON-RPC over a plain <see cref="HttpClient"/> (<c>getBalance</c>, <c>getLatestBlockhash</c>,
    /// <c>sendTransaction</c>) - no Solnet.Rpc dependency in BiatecMCP, same approach as <see cref="PublicEvmRpcDataSource"/>.
    /// The node URL comes from <see cref="SolanaConfiguration.RpcUrl"/>.
    /// </remarks>
    public sealed class SolanaRpcDataSource : ISolanaRpcDataSource
    {
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

        private readonly HttpClient _httpClient;
        private readonly IOptionsMonitor<SolanaConfiguration> _config;
        private readonly ILogger<SolanaRpcDataSource> _logger;

        public SolanaRpcDataSource(HttpClient httpClient, IOptionsMonitor<SolanaConfiguration> config, ILogger<SolanaRpcDataSource> logger)
        {
            _httpClient = httpClient;
            _config = config;
            _logger = logger;
        }

        public async Task<ulong?> TryGetBalanceAsync(string address, CancellationToken cancellationToken = default)
        {
            var response = await TryCallAsync("getBalance", new object[] { address, new { commitment = "confirmed" } }, cancellationToken);
            if (response?.Result is { } result && result.TryGetProperty("value", out var value) && value.TryGetUInt64(out var lamports))
            {
                return lamports;
            }

            return null;
        }

        public async Task<string?> TryGetLatestBlockhashAsync(CancellationToken cancellationToken = default)
        {
            var response = await TryCallAsync("getLatestBlockhash", new object[] { new { commitment = "finalized" } }, cancellationToken);
            if (response?.Result is { } result
                && result.TryGetProperty("value", out var value)
                && value.TryGetProperty("blockhash", out var blockhash)
                && blockhash.ValueKind == JsonValueKind.String)
            {
                return blockhash.GetString();
            }

            return null;
        }

        public async Task<SolanaSendResult> SendTransactionAsync(byte[] signedTransaction, CancellationToken cancellationToken = default)
        {
            // preflightCommitment matches the "confirmed" commitment createSolanaTransaction's balance pre-check uses - a
            // balance that is confirmed but not yet finalized would otherwise pass the pre-check and then fail preflight.
            var response = await TryCallAsync(
                "sendTransaction",
                new object[] { Convert.ToBase64String(signedTransaction), new { encoding = "base64", preflightCommitment = "confirmed" } },
                cancellationToken);

            if (response == null)
            {
                // A timeout / dropped connection / error status after the request left the building does NOT prove the node
                // dropped the transaction - it may have been accepted. Say so, and name the signature to look up, so an agent
                // doesn't rebuild and resend a second transfer.
                return new SolanaSendResult(null, "The Solana RPC node did not answer cleanly, so it is UNKNOWN whether the transaction was accepted.", OutcomeUnknown: true, ExpectedSignature: SolanaAddressValidator.TryComputeSignature(signedTransaction));
            }

            if (response.Error != null)
            {
                // A preflight failure's useful detail (e.g. "AccountNotFound", "InsufficientFundsForFee") is in
                // error.data.err, not in the generic "Transaction simulation failed" message.
                var message = response.Error.Message ?? "The Solana RPC node rejected the transaction.";
                return new SolanaSendResult(null, response.Error.Data is { } data && data.ValueKind == JsonValueKind.Object && data.TryGetProperty("err", out var err) && err.ValueKind != JsonValueKind.Null
                    ? $"{message} ({err.GetRawText().Trim('"')})"
                    : message);
            }

            return response.Result is { ValueKind: JsonValueKind.String } signature && !string.IsNullOrEmpty(signature.GetString())
                ? new SolanaSendResult(signature.GetString(), null)
                : new SolanaSendResult(null, "The Solana RPC node returned no transaction signature.");
        }

        private async Task<JsonRpcResponse?> TryCallAsync(string method, object[] parameters, CancellationToken cancellationToken)
        {
            try
            {
                var request = new JsonRpcRequest { Method = method, Params = parameters };
                using var response = await _httpClient.PostAsJsonAsync(_config.CurrentValue.RpcUrl, request, JsonOptions, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Solana RPC {Method} returned HTTP {Status}.", method, (int)response.StatusCode);
                    return null;
                }

                return await response.Content.ReadFromJsonAsync<JsonRpcResponse>(JsonOptions, cancellationToken);
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
            {
                _logger.LogWarning(ex, "Solana RPC {Method} failed.", method);
                return null;
            }
        }

        private sealed class JsonRpcRequest
        {
            public string Jsonrpc { get; set; } = "2.0";
            public string Method { get; set; } = string.Empty;
            public object[] Params { get; set; } = Array.Empty<object>();
            public int Id { get; set; } = 1;
        }

        private sealed class JsonRpcResponse
        {
            public JsonElement? Result { get; set; }

            [JsonPropertyName("error")]
            public JsonRpcError? Error { get; set; }
        }

        private sealed class JsonRpcError
        {
            public string? Message { get; set; }

            public JsonElement? Data { get; set; }
        }
    }
}
