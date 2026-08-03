using System.Net.Http.Headers;
using BiatecMCP.Model;
using Microsoft.AspNetCore.Mvc;

namespace BiatecMCP.BusinessLogic
{
    /// <inheritdoc cref="IBiatecWalletClient"/>
    /// <remarks>
    /// Typed <see cref="HttpClient"/> (registered via <c>AddHttpClient&lt;BiatecWalletClient&gt;()</c>,
    /// <c>BaseAddress</c> configured from <c>Oidc:Issuer</c> at registration time in <c>Program.cs</c> -
    /// same idiom BiatecOIDC's own <c>BiatecRouterQuoteClient</c> uses for its own outbound HTTP dependency).
    /// </remarks>
    public sealed class BiatecWalletClient : IBiatecWalletClient
    {
        private readonly HttpClient _httpClient;

        public BiatecWalletClient(HttpClient httpClient)
        {
            _httpClient = httpClient;
        }

        public async Task<SignTransactionGroupResponse> SignAsync(string bearerToken, IReadOnlyList<byte[]> unsignedTransactions, string? primaryAddress = null, int slot = 0, CancellationToken cancellationToken = default)
        {
            var request = new SignTransactionGroupRequest
            {
                Transactions = unsignedTransactions.Select(Convert.ToBase64String).ToList(),
                PrimaryAddress = primaryAddress,
                Slot = slot
            };

            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "wallet/sign")
            {
                Content = JsonContent.Create(request)
            };
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);

            using var response = await _httpClient.SendAsync(httpRequest, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw await BuildExceptionAsync(response, cancellationToken);
            }

            return await response.Content.ReadFromJsonAsync<SignTransactionGroupResponse>(cancellationToken)
                ?? new SignTransactionGroupResponse();
        }

        public async Task<ListSeedsResponse> ListSeedsAsync(string bearerToken, CancellationToken cancellationToken = default)
        {
            using var httpRequest = new HttpRequestMessage(HttpMethod.Get, "wallet/seeds");
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);

            using var response = await _httpClient.SendAsync(httpRequest, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw await BuildExceptionAsync(response, cancellationToken);
            }

            return await response.Content.ReadFromJsonAsync<ListSeedsResponse>(cancellationToken)
                ?? new ListSeedsResponse();
        }

        public async Task<ListAddressesResponse> ListAddressesAsync(string bearerToken, CancellationToken cancellationToken = default)
        {
            using var httpRequest = new HttpRequestMessage(HttpMethod.Get, "wallet/address");
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);

            using var response = await _httpClient.SendAsync(httpRequest, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw await BuildExceptionAsync(response, cancellationToken);
            }

            return await response.Content.ReadFromJsonAsync<ListAddressesResponse>(cancellationToken)
                ?? new ListAddressesResponse();
        }

        public async Task<DerivedAddressResponse> GetAddressAsync(string bearerToken, string primaryAddress, int slot, CancellationToken cancellationToken = default)
        {
            using var httpRequest = new HttpRequestMessage(HttpMethod.Get, $"wallet/address/{Uri.EscapeDataString(primaryAddress)}/{slot}");
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);

            using var response = await _httpClient.SendAsync(httpRequest, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw await BuildExceptionAsync(response, cancellationToken);
            }

            return await response.Content.ReadFromJsonAsync<DerivedAddressResponse>(cancellationToken)
                ?? new DerivedAddressResponse();
        }

        public async Task<ListEvmAddressesResponse> ListEvmAddressesAsync(string bearerToken, CancellationToken cancellationToken = default)
        {
            using var httpRequest = new HttpRequestMessage(HttpMethod.Get, "wallet/evm/address");
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);

            using var response = await _httpClient.SendAsync(httpRequest, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw await BuildExceptionAsync(response, cancellationToken);
            }

            return await response.Content.ReadFromJsonAsync<ListEvmAddressesResponse>(cancellationToken)
                ?? new ListEvmAddressesResponse();
        }

        public async Task<DerivedEvmAddressResponse> GetEvmAddressAsync(string bearerToken, string primaryAddress, int slot, CancellationToken cancellationToken = default)
        {
            using var httpRequest = new HttpRequestMessage(HttpMethod.Get, $"wallet/evm/address/{Uri.EscapeDataString(primaryAddress)}/{slot}");
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);

            using var response = await _httpClient.SendAsync(httpRequest, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw await BuildExceptionAsync(response, cancellationToken);
            }

            return await response.Content.ReadFromJsonAsync<DerivedEvmAddressResponse>(cancellationToken)
                ?? new DerivedEvmAddressResponse();
        }

        private static async Task<WalletApiException> BuildExceptionAsync(HttpResponseMessage response, CancellationToken cancellationToken)
        {
            ProblemDetails? problem = null;
            try
            {
                problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(cancellationToken);
            }
            catch
            {
                // Non-ProblemDetails error body (e.g. an upstream proxy/5xx page) - fall through to the
                // generic status-code-derived error below rather than letting the parse failure mask the
                // real (already-known) failure.
            }

            var errorCode = problem?.Title ?? response.StatusCode.ToString();
            return new WalletApiException((int)response.StatusCode, errorCode, problem?.Detail);
        }
    }
}
