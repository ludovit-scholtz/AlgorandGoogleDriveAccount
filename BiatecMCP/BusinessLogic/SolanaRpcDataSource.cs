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
            var response = await TryCallAsync("getLatestBlockhash", new object[] { new { commitment = "confirmed" } }, cancellationToken);
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
            var httpStatus = 0;
            var response = await TryCallAsync(
                "sendTransaction",
                new object[] { Convert.ToBase64String(signedTransaction), new { encoding = "base64", preflightCommitment = "confirmed" } },
                cancellationToken,
                status => httpStatus = status);

            if (response == null)
            {
                // A 4xx (rate limit, forbidden, ...) is a definitive "not accepted" - the node refused to even look at it.
                if (httpStatus is >= 400 and < 500)
                {
                    return new SolanaSendResult(null, $"The Solana RPC node refused the request (HTTP {httpStatus}, most likely rate limiting). The transaction was not accepted - submit the same signed transaction again shortly.");
                }

                // A timeout / dropped connection / 5xx after the request left the building does NOT prove the node dropped the
                // transaction - it may have been accepted. Say so, and name the signature to look up, so an agent doesn't
                // rebuild and send a second transfer.
                return new SolanaSendResult(null, "The Solana RPC node did not answer cleanly, so it is UNKNOWN whether the transaction was accepted.", OutcomeUnknown: true, ExpectedSignature: SolanaAddressValidator.TryComputeSignature(signedTransaction));
            }

            if (response.Error != null)
            {
                // Resubmitting the exact same signed bytes after an outcome-unknown broadcast is the advertised safe retry. If
                // the first attempt did land, the node answers "already processed" - which is a SUCCESS (same signature), not a
                // failed transfer an agent might then rebuild and send twice.
                if (IsAlreadyProcessed(response.Error) && SolanaAddressValidator.TryComputeSignature(signedTransaction) is { } processedSignature)
                {
                    return new SolanaSendResult(processedSignature, null);
                }

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

        private static bool IsAlreadyProcessed(JsonRpcError error) =>
            (error.Message?.Contains("already been processed", StringComparison.OrdinalIgnoreCase) ?? false)
            || (error.Data is { ValueKind: JsonValueKind.Object } data
                && data.TryGetProperty("err", out var err)
                && err.ValueKind == JsonValueKind.String
                && string.Equals(err.GetString(), "AlreadyProcessed", StringComparison.OrdinalIgnoreCase));

        private async Task<JsonRpcResponse?> TryCallAsync(string method, object[] parameters, CancellationToken cancellationToken, Action<int>? onHttpError = null)
        {
            try
            {
                var request = new JsonRpcRequest { Method = method, Params = parameters };
                using var response = await _httpClient.PostAsJsonAsync(_config.CurrentValue.RpcUrl, request, JsonOptions, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Solana RPC {Method} returned HTTP {Status}.", method, (int)response.StatusCode);
                    onHttpError?.Invoke((int)response.StatusCode);
                    return null;
                }

                return await response.Content.ReadFromJsonAsync<JsonRpcResponse>(JsonOptions, cancellationToken);
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or NotSupportedException || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
            {
                // A caller-initiated cancellation is not an RPC failure - it propagates as the cancellation it is.
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
