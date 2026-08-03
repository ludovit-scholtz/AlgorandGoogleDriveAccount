using System.Net.Http.Headers;
using System.Text.Json;
using BiatecMCP.Model;
using Microsoft.AspNetCore.Mvc;

namespace BiatecMCP.BusinessLogic
{
    /// <inheritdoc cref="IBiatecWalletClient"/>
    /// <remarks>
    /// Wraps <see cref="Generated.OidcApiClient"/> - the NSwag-generated client produced from BiatecOIDC's
    /// own published OpenAPI document (<c>GET /swagger/v1/swagger.json</c>, see <c>Generated/OidcApiClient.g.cs</c>'s
    /// header comment for the exact regeneration command) - so every actual HTTP call this project makes to
    /// BiatecOIDC's wallet API goes through that generated client rather than hand-written request/response
    /// plumbing. This class stays as the seam <c>IBiatecWalletClient</c> callers (the MCP tools, and their
    /// tests) depend on: it translates BiatecOIDC's generated DTOs into this project's own
    /// <see cref="BiatecMCP.Model"/> types (kept, rather than referencing the generated types directly,
    /// for the same "no compile-time coupling between the two independently-deployed services" reasoning
    /// as every other duplicated model in this project - the generated client is regenerated from a
    /// published contract, not a project reference) and translates <see cref="Generated.OidcApiException"/>
    /// into this project's own <see cref="WalletApiException"/>.
    ///
    /// The generated client takes no bearer token parameter (BiatecOIDC's OpenAPI document doesn't model
    /// per-call auth), so it's set as the shared <see cref="HttpClient"/>'s <c>Authorization</c> header
    /// immediately before each call - safe here because <c>AddHttpClient&lt;IBiatecWalletClient,
    /// BiatecWalletClient&gt;()</c> hands out a new instance per resolution (see <c>Program.cs</c>) and every
    /// call on a single instance is awaited before the next one starts, so two different bearer tokens are
    /// never in flight on the same underlying <see cref="HttpClient"/> at once.
    /// </remarks>
    public sealed class BiatecWalletClient : IBiatecWalletClient
    {
        private readonly HttpClient _httpClient;
        private readonly Generated.OidcApiClient _client;

        public BiatecWalletClient(HttpClient httpClient)
        {
            _httpClient = httpClient;
            _client = new Generated.OidcApiClient(httpClient);
        }

        public async Task<SignTransactionGroupResponse> SignAsync(string bearerToken, string network, string address, IReadOnlyList<byte[]> unsignedTransactions, CancellationToken cancellationToken = default)
        {
            Authenticate(bearerToken);
            var request = new Generated.SignTransactionGroupRequest
            {
                Transactions = unsignedTransactions.Select(Convert.ToBase64String).ToList()
            };

            var result = await CallAsync(() => _client.SignAsync(network, address, request, cancellationToken));
            return new SignTransactionGroupResponse { SignedTransactions = result.SignedTransactions?.ToList() ?? new() };
        }

        public async Task<ListSeedsResponse> ListSeedsAsync(string bearerToken, CancellationToken cancellationToken = default)
        {
            Authenticate(bearerToken);
            var result = await CallAsync(() => _client.SeedsGETAsync(cancellationToken));
            return new ListSeedsResponse
            {
                Seeds = result.Seeds?.Select(s => new SeedResponse { Address = s.Address, CreatedUtc = s.CreatedUtc, IsPrimary = s.IsPrimary }).ToList() ?? new()
            };
        }

        public async Task<DerivedAddressResponse> GetAddressAsync(string bearerToken, string seedAddress, int slot, CancellationToken cancellationToken = default)
        {
            Authenticate(bearerToken);
            var result = await CallAsync(() => _client.AddressAsync(seedAddress, slot, cancellationToken));
            return new DerivedAddressResponse
            {
                Address = result.Address,
                EvmAddress = result.EvmAddress,
                BitcoinAddress = result.BitcoinAddress,
                BitcoinCashAddress = result.BitcoinCashAddress,
                SeedAddress = result.SeedAddress,
                Slot = result.Slot
            };
        }

        public async Task<AddressInfoResponse> GetAddressInfoAsync(string bearerToken, string network, string address, CancellationToken cancellationToken = default)
        {
            Authenticate(bearerToken);
            var result = await CallAsync(() => _client.InfoAsync(network, address, cancellationToken));
            return ToAddressInfoResponse(result);
        }

        public async Task<AddressInfoResponse> ActivateAddressAsync(string bearerToken, string network, string seedAddress, int slot, string address, CancellationToken cancellationToken = default)
        {
            Authenticate(bearerToken);
            var request = new Generated.ActivateAddressRequest { Address = address };
            var result = await CallAsync(() => _client.ActivateAsync(network, seedAddress, slot, request, cancellationToken));
            return ToAddressInfoResponse(result);
        }

        public async Task<ListActiveAddressesResponse> ListActiveAddressesAsync(string bearerToken, CancellationToken cancellationToken = default)
        {
            Authenticate(bearerToken);
            var result = await CallAsync(() => _client.ActiveAddressesAsync(cancellationToken));
            return new ListActiveAddressesResponse
            {
                Addresses = result.Addresses?.Select(a => new ActiveAddressResponse
                {
                    Address = a.Address,
                    Family = a.Family,
                    SeedAddress = a.SeedAddress,
                    Slot = a.Slot,
                    ActivatedUtc = a.ActivatedUtc
                }).ToList() ?? new()
            };
        }

        private static AddressInfoResponse ToAddressInfoResponse(Generated.AddressInfoResponse result) => new()
        {
            Address = result.Address,
            Network = result.Network,
            Family = result.Family,
            IsActive = result.IsActive,
            SeedAddress = result.SeedAddress,
            Slot = result.Slot
        };

        private void Authenticate(string bearerToken)
        {
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        }

        /// <summary>Runs a generated-client call, translating <see cref="Generated.OidcApiException"/> into <see cref="WalletApiException"/>.</summary>
        private static async Task<T> CallAsync<T>(Func<Task<T>> call)
        {
            try
            {
                return await call();
            }
            catch (Generated.OidcApiException ex)
            {
                throw BuildException(ex);
            }
        }

        private static WalletApiException BuildException(Generated.OidcApiException ex)
        {
            ProblemDetails? problem = null;
            try
            {
                problem = string.IsNullOrWhiteSpace(ex.Response) ? null : JsonSerializer.Deserialize<ProblemDetails>(ex.Response);
            }
            catch (JsonException)
            {
                // Non-ProblemDetails error body (e.g. an upstream proxy/5xx page) - fall through to the
                // generic status-code-derived error below rather than letting the parse failure mask the
                // real (already-known) failure.
            }

            var errorCode = problem?.Title ?? ((System.Net.HttpStatusCode)ex.StatusCode).ToString();
            return new WalletApiException(ex.StatusCode, errorCode, problem?.Detail);
        }
    }
}
