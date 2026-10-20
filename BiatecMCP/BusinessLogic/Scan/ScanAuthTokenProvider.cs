using Algorand;
using Algorand.Algod;
using Algorand.Algod.Model;
using Algorand.Algod.Model.Transactions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using BiatecMCP.Model;

namespace BiatecMCP.BusinessLogic.Scan
{
    /// <summary>Produces the ARC-14 <c>Authorization</c> header value Biatec Scan's read API requires.</summary>
    public interface IScanAuthTokenProvider
    {
        Task<string> GetAuthorizationHeaderAsync(AlgorandChain chain, CancellationToken cancellationToken = default);

        /// <summary>Forgets the cached token of a network (after Scan rejected it), so the next call mints a fresh one.</summary>
        void Invalidate(AlgorandChain chain);
    }

    /// <summary>
    /// ARC-14 ("Algorand transaction signature as authentication"): a zero-fee, never-broadcast self-payment whose note is the API's realm,
    /// signed by the caller and sent as <c>SigTx &lt;base64&gt;</c>; its validity window is the token's lifetime. Biatec Scan's own web app
    /// uses a throwaway anonymous identity for this, and so does BiatecMCP: one random keypair generated in memory per process, never
    /// persisted, never funded and never able to sign anything but this proof - it is an API-access identity, not a wallet key, so BiatecMCP
    /// still holds no wallet key material and the Scan tools need neither BiatecOIDC nor the <c>sign</c> scope.
    /// </summary>
    public sealed class Arc14ScanAuthTokenProvider : IScanAuthTokenProvider, IDisposable
    {
        /// <summary>The token is reused for this long; the ~1000-round (about 46 minutes) validity window comfortably outlives it.</summary>
        private static readonly TimeSpan TokenReuse = TimeSpan.FromMinutes(15);

        private readonly Algorand.Algod.Model.Account _identity = new();
        private readonly IMemoryCache _cache;
        private readonly ScanConfiguration _configuration;
        private readonly Func<AlgorandChain, CancellationToken, Task<TransactionParametersResponse>> _getParams;
        private readonly SemaphoreSlim _mint = new(1, 1);

        public Arc14ScanAuthTokenProvider(IMemoryCache cache, IOptions<ScanConfiguration> options)
            : this(cache, options, FetchParamsAsync)
        {
        }

        internal Arc14ScanAuthTokenProvider(
            IMemoryCache cache,
            IOptions<ScanConfiguration> options,
            Func<AlgorandChain, CancellationToken, Task<TransactionParametersResponse>> getParams)
        {
            _cache = cache;
            _configuration = options.Value;
            _getParams = getParams;
        }

        public async Task<string> GetAuthorizationHeaderAsync(AlgorandChain chain, CancellationToken cancellationToken = default)
        {
            var key = "scan-arc14:" + chain.GenesisId;
            if (_cache.TryGetValue(key, out string? cached) && cached != null)
            {
                return cached;
            }

            await _mint.WaitAsync(cancellationToken);
            try
            {
                if (_cache.TryGetValue(key, out cached) && cached != null)
                {
                    return cached;
                }

                var parameters = await _getParams(chain, cancellationToken);
                parameters.MinFee = 0;
                var transaction = PaymentTransaction.GetPaymentTransactionFromNetworkTransactionParameters(
                    _identity.Address, _identity.Address, 0, _configuration.Realm, parameters);
                transaction.Fee = 0;
                var signed = transaction.Sign(_identity);
                var header = "SigTx " + Convert.ToBase64String(Algorand.Utils.Encoder.EncodeToMsgPackOrdered(signed));
                _cache.Set(key, header, TokenReuse);
                return header;
            }
            finally
            {
                _mint.Release();
            }
        }

        public void Invalidate(AlgorandChain chain) => _cache.Remove("scan-arc14:" + chain.GenesisId);

        public void Dispose() => _mint.Dispose();

        private static async Task<TransactionParametersResponse> FetchParamsAsync(AlgorandChain chain, CancellationToken cancellationToken)
        {
            // Short timeout: minting holds a lock, so a hanging node must not stall every Scan call for the default 100 s.
            using var http = string.IsNullOrWhiteSpace(chain.AlgodApiTokenHeader)
                ? HttpClientConfigurator.ConfigureHttpClient(chain.AlgodApiAddress, chain.AlgodApiToken, timeout: 10_000)
                : HttpClientConfigurator.ConfigureHttpClient(chain.AlgodApiAddress, chain.AlgodApiToken, chain.AlgodApiTokenHeader, timeout: 10_000);
            return await new DefaultApi(http).TransactionParamsAsync(cancellationToken);
        }
    }
}
