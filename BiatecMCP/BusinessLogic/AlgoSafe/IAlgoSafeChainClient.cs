using Algorand;
using Algorand.Algod;
using Algorand.Algod.Model;
using Algorand.Algod.Model.Transactions;

namespace BiatecMCP.BusinessLogic.AlgoSafe
{
    /// <summary>
    /// The handful of algod operations the AlgoSafe tools need - a seam over <see cref="DefaultApi"/> so the build/read logic is
    /// unit-testable without a node. <see cref="AlgodAlgoSafeChainClient"/> is the real implementation. Every "not found" is a
    /// <c>null</c> result, never an exception.
    /// </summary>
    public interface IAlgoSafeChainClient : IDisposable
    {
        /// <summary>The algod client the generated contract proxies are constructed with (they never use it for transaction parameters - those are injected).</summary>
        DefaultApi Algod { get; }

        Task<TransactionParametersResponse> GetTransactionParamsAsync(CancellationToken cancellationToken = default);

        /// <summary>The application, or <c>null</c> if it doesn't exist.</summary>
        Task<Application?> GetApplicationAsync(ulong appId, CancellationToken cancellationToken = default);

        /// <summary>A box's value, or <c>null</c> if the box (or the app) doesn't exist.</summary>
        Task<byte[]?> GetBoxAsync(ulong appId, byte[] name, CancellationToken cancellationToken = default);

        /// <summary>Every box name of the application. Throws if the node's result is capped (a truncated listing must not be mistaken for the full set).</summary>
        Task<IReadOnlyList<byte[]>> ListBoxNamesAsync(ulong appId, CancellationToken cancellationToken = default);

        /// <summary>Simulates the (unsigned) group with empty signatures allowed, unnamed resources allowed (so the missing references are reported) and the execution trace enabled.</summary>
        Task<SimulateTransactionGroupResult> SimulateAsync(IReadOnlyList<Transaction> group, CancellationToken cancellationToken = default);

        /// <summary>The pending/confirmed transaction, or <c>null</c> if algod doesn't know it.</summary>
        Task<IReturnableTransaction?> GetPendingTransactionAsync(string transactionId, CancellationToken cancellationToken = default);

        /// <summary>The account's balance in microAlgo (0 for an account that doesn't exist yet).</summary>
        Task<ulong> GetBalanceAsync(Address address, CancellationToken cancellationToken = default);
    }

    /// <summary>Creates a chain client for one resolved AVM network.</summary>
    public interface IAlgoSafeChainClientFactory
    {
        IAlgoSafeChainClient Create(AlgorandChain chain);
    }
}
