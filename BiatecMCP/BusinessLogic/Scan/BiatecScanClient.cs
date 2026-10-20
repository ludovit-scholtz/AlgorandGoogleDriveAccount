using System.Globalization;
using System.Net;
using System.Text.Json;
using BiatecMCP.Model;
using Microsoft.Extensions.Options;

namespace BiatecMCP.BusinessLogic.Scan
{
    /// <summary>Read access to Biatec Scan (AVMTradeReporter) - assets, pools and per-asset stats of one network.</summary>
    public interface IBiatecScanClient
    {
        /// <summary>The network codes a Scan deployment is configured for.</summary>
        IReadOnlyCollection<string> ConfiguredNetworks { get; }

        bool IsConfigured(string networkCode);

        Task<IReadOnlyList<ScanAsset>> SearchAssetsAsync(string networkCode, AlgorandChain chain, string search, int size, CancellationToken cancellationToken = default);

        Task<IReadOnlyList<ScanAsset>> GetAssetsByIdAsync(string networkCode, AlgorandChain chain, IEnumerable<ulong> ids, CancellationToken cancellationToken = default);

        /// <summary>Pools touching <paramref name="assetId"/> (on either side), newest-updated first.</summary>
        Task<IReadOnlyList<ScanPool>> GetPoolsForAssetAsync(string networkCode, AlgorandChain chain, ulong assetId, string? protocol, int size, CancellationToken cancellationToken = default);

        /// <summary>Pools of exactly asset A / asset B in that pool order (Scan matches the pair order exactly when both are given).</summary>
        Task<IReadOnlyList<ScanPool>> GetPoolsForPairAsync(string networkCode, AlgorandChain chain, ulong assetIdA, ulong assetIdB, string? protocol, int size, CancellationToken cancellationToken = default);

        Task<IReadOnlyList<ScanAssetStat>> GetAssetStatsAsync(string networkCode, AlgorandChain chain, string? protocol, string sortBy, CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Typed <see cref="HttpClient"/> over Biatec Scan's REST API. <c>/api/asset</c> is public; the pool and stat endpoints need the ARC-14
    /// header from <see cref="IScanAuthTokenProvider"/>, which is sent on every call (harmless where it isn't required).
    /// </summary>
    public sealed class BiatecScanClient : IBiatecScanClient
    {
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        private readonly HttpClient _http;
        private readonly IScanAuthTokenProvider _auth;
        private readonly Dictionary<string, string> _networks;

        public BiatecScanClient(HttpClient http, IScanAuthTokenProvider auth, IOptions<ScanConfiguration> options)
        {
            _http = http;
            _auth = auth;
            _networks = new Dictionary<string, string>(options.Value.Networks, StringComparer.OrdinalIgnoreCase);
        }

        public IReadOnlyCollection<string> ConfiguredNetworks => _networks.Keys;

        public bool IsConfigured(string networkCode) => _networks.ContainsKey(networkCode);

        public Task<IReadOnlyList<ScanAsset>> SearchAssetsAsync(string networkCode, AlgorandChain chain, string search, int size, CancellationToken cancellationToken = default) =>
            GetAsync<ScanAsset>(networkCode, chain, $"/api/asset?search={Uri.EscapeDataString(search)}&size={size.ToString(CultureInfo.InvariantCulture)}", cancellationToken);

        public Task<IReadOnlyList<ScanAsset>> GetAssetsByIdAsync(string networkCode, AlgorandChain chain, IEnumerable<ulong> ids, CancellationToken cancellationToken = default)
        {
            var list = string.Join(",", ids.Distinct().Select(i => i.ToString(CultureInfo.InvariantCulture)));
            return string.IsNullOrEmpty(list)
                ? Task.FromResult<IReadOnlyList<ScanAsset>>(Array.Empty<ScanAsset>())
                : GetAsync<ScanAsset>(networkCode, chain, $"/api/asset?ids={list}", cancellationToken);
        }

        public Task<IReadOnlyList<ScanPool>> GetPoolsForAssetAsync(string networkCode, AlgorandChain chain, ulong assetId, string? protocol, int size, CancellationToken cancellationToken = default)
        {
            var path = $"/api/pool?assetIdA={assetId.ToString(CultureInfo.InvariantCulture)}&size={size.ToString(CultureInfo.InvariantCulture)}";
            if (!string.IsNullOrEmpty(protocol))
            {
                path += "&protocol=" + Uri.EscapeDataString(protocol);
            }

            return GetAsync<ScanPool>(networkCode, chain, path, cancellationToken);
        }

        public Task<IReadOnlyList<ScanPool>> GetPoolsForPairAsync(string networkCode, AlgorandChain chain, ulong assetIdA, ulong assetIdB, string? protocol, int size, CancellationToken cancellationToken = default)
        {
            var path = $"/api/pool?assetIdA={assetIdA.ToString(CultureInfo.InvariantCulture)}&assetIdB={assetIdB.ToString(CultureInfo.InvariantCulture)}&size={size.ToString(CultureInfo.InvariantCulture)}";
            if (!string.IsNullOrEmpty(protocol))
            {
                path += "&protocol=" + Uri.EscapeDataString(protocol);
            }

            return GetAsync<ScanPool>(networkCode, chain, path, cancellationToken);
        }

        public Task<IReadOnlyList<ScanAssetStat>> GetAssetStatsAsync(string networkCode, AlgorandChain chain, string? protocol, string sortBy, CancellationToken cancellationToken = default)
        {
            var path = $"/api/asset-stat?sortBy={Uri.EscapeDataString(sortBy)}";
            if (!string.IsNullOrEmpty(protocol))
            {
                path += "&protocol=" + Uri.EscapeDataString(protocol);
            }

            return GetAsync<ScanAssetStat>(networkCode, chain, path, cancellationToken);
        }

        private async Task<IReadOnlyList<T>> GetAsync<T>(string networkCode, AlgorandChain chain, string path, CancellationToken cancellationToken)
        {
            if (!_networks.TryGetValue(networkCode, out var baseUrl))
            {
                throw new ScanApiException($"Biatec Scan is not available for network '{networkCode}'. Configured networks: {string.Join(", ", _networks.Keys)}.");
            }

            try
            {
                var (response, header) = await SendAsync(baseUrl, path, chain, cancellationToken);
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    // The cached token may be stale (e.g. its validity window drifted from Scan's view of the chain): mint a fresh one, once.
                    response.Dispose();
                    _auth.Invalidate(chain, header);
                    (response, _) = await SendAsync(baseUrl, path, chain, cancellationToken);
                }

                using var finalResponse = response;
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    throw new ScanApiException("Biatec Scan rejected the request's authentication, even with a freshly signed token.");
                }

                if (!response.IsSuccessStatusCode)
                {
                    throw new ScanApiException($"Biatec Scan answered HTTP {(int)response.StatusCode}.");
                }

                var result = await response.Content.ReadFromJsonAsync<List<T>>(Json, cancellationToken);
                return result ?? new List<T>();
            }
            catch (HttpRequestException)
            {
                throw new ScanApiException("Biatec Scan could not be reached.");
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new ScanApiException("Biatec Scan timed out.");
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException)
            {
                throw new ScanApiException("Biatec Scan returned an unreadable answer.");
            }
        }

        private async Task<(HttpResponseMessage Response, string Header)> SendAsync(string baseUrl, string path, AlgorandChain chain, CancellationToken cancellationToken)
        {
            string authorization;
            try
            {
                authorization = await _auth.GetAuthorizationHeaderAsync(chain, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new ScanApiException("Could not prepare the authentication for Biatec Scan (the network's node is unreachable).");
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, baseUrl.TrimEnd('/') + path);
            request.Headers.TryAddWithoutValidation("Authorization", authorization);
            return (await _http.SendAsync(request, cancellationToken), authorization);
        }
    }
}
