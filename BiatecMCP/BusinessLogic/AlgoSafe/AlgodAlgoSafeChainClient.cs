using Algorand;
using Algorand.Algod;
using Algorand.Algod.Model;
using Algorand.Algod.Model.Transactions;

namespace BiatecMCP.BusinessLogic.AlgoSafe
{
    /// <summary>Real <see cref="IAlgoSafeChainClient"/> over algod. Owns the <see cref="HttpClient"/> it was created with.</summary>
    public sealed class AlgodAlgoSafeChainClient : IAlgoSafeChainClient
    {
        /// <summary>The node's box listing is capped; a result that fills the request exactly may be truncated.</summary>
        private const ulong BoxListingCap = 10_000;

        private readonly HttpClient _httpClient;

        public AlgodAlgoSafeChainClient(HttpClient httpClient)
        {
            _httpClient = httpClient;
            Algod = new DefaultApi(httpClient);
        }

        public DefaultApi Algod { get; }

        public Task<TransactionParametersResponse> GetTransactionParamsAsync(CancellationToken cancellationToken = default) =>
            Algod.TransactionParamsAsync(cancellationToken);

        public async Task<Application?> GetApplicationAsync(ulong appId, CancellationToken cancellationToken = default)
        {
            try
            {
                return await Algod.GetApplicationByIDAsync(cancellationToken, appId);
            }
            catch (ApiException ex) when (ex.StatusCode == 404)
            {
                return null;
            }
            catch (ApiException<ErrorResponse> ex) when (ex.StatusCode == 404)
            {
                return null;
            }
        }

        public async Task<byte[]?> GetBoxAsync(ulong appId, byte[] name, CancellationToken cancellationToken = default)
        {
            try
            {
                var box = await Algod.GetApplicationBoxByNameAsync(cancellationToken, appId, "b64:" + Convert.ToBase64String(name));
                return box.Value;
            }
            catch (ApiException ex) when (ex.StatusCode == 404)
            {
                return null;
            }
            catch (ApiException<ErrorResponse> ex) when (ex.StatusCode == 404)
            {
                return null;
            }
        }

        public async Task<IReadOnlyList<byte[]>> ListBoxNamesAsync(ulong appId, CancellationToken cancellationToken = default)
        {
            BoxesResponse response;
            try
            {
                response = await Algod.GetApplicationBoxesAsync(cancellationToken, appId, BoxListingCap);
            }
            catch (ApiException ex) when (ex.StatusCode == 400)
            {
                // algod answers 400 (rather than a truncated list) when more boxes exist than the requested maximum.
                throw new AlgoSafeException("TooManyBoxes", $"Application {appId} has more than {BoxListingCap} boxes - too many to enumerate safely.");
            }

            var names = response.Boxes?.Select(b => b.Name).ToList() ?? new List<byte[]>();
            if ((ulong)names.Count >= BoxListingCap)
            {
                throw new AlgoSafeException("TooManyBoxes", $"Application {appId} has at least {BoxListingCap} boxes - too many to enumerate safely.");
            }

            return names;
        }

        public async Task<SimulateTransactionGroupResult> SimulateAsync(IReadOnlyList<Transaction> group, CancellationToken cancellationToken = default)
        {
            var response = await Algod.SimulateTransactionAsync(cancellationToken, new SimulateRequest
            {
                AllowEmptySignatures = true,
                AllowUnnamedResources = true,
                ExecTraceConfig = new SimulateTraceConfig { Enable = true },
                TxnGroups = new List<SimulateRequestTransactionGroup>
                {
                    new() { Txns = group.Select(t => new SignedTransaction { Tx = t }).ToList() }
                }
            });

            return response.TxnGroups.First();
        }

        public async Task<IReturnableTransaction?> GetPendingTransactionAsync(string transactionId, CancellationToken cancellationToken = default)
        {
            try
            {
                return await Algod.PendingTransactionInformationAsync(cancellationToken, transactionId);
            }
            catch (ApiException ex) when (ex.StatusCode == 404)
            {
                return null;
            }
            catch (ApiException<ErrorResponse> ex) when (ex.StatusCode == 404)
            {
                return null;
            }
        }

        public async Task<ulong> GetBalanceAsync(Address address, CancellationToken cancellationToken = default)
        {
            var account = await Algod.AccountInformationAsync(cancellationToken, address.EncodeAsString(), null, null);
            return account.Amount;
        }

        public void Dispose() => _httpClient.Dispose();
    }

    /// <summary>Creates <see cref="AlgodAlgoSafeChainClient"/>s against a resolved chain's algod endpoint.</summary>
    public sealed class AlgodAlgoSafeChainClientFactory : IAlgoSafeChainClientFactory
    {
        public IAlgoSafeChainClient Create(AlgorandChain chain) =>
            new AlgodAlgoSafeChainClient(HttpClientConfigurator.ConfigureHttpClient(chain.AlgodApiAddress, chain.AlgodApiToken));
    }
}
