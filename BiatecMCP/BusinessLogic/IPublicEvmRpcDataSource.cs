using System.Numerics;

namespace BiatecMCP.BusinessLogic
{
    /// <summary>
    /// Raw access to the public EVM chain/RPC registry (https://chainid.network/chains.json) and to a
    /// resolved RPC's own JSON-RPC endpoint - separated from <see cref="IEvmChainRegistry"/>'s
    /// selection/caching logic purely so that logic can be unit-tested with canned data instead of live
    /// HTTP calls. <see cref="PublicEvmRpcDataSource"/> is the real implementation; nothing else should
    /// implement this outside tests.
    /// </summary>
    public interface IPublicEvmRpcDataSource
    {
        Task<IReadOnlyList<EvmChainListEntry>> GetChainListAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Calls <paramref name="rpcUrl"/>'s own <c>eth_chainId</c> JSON-RPC method and returns the chain id
        /// it reports, or <c>null</c> if the node is unreachable/errors - never throws.
        /// </summary>
        Task<long?> TryGetLiveChainIdAsync(string rpcUrl, CancellationToken cancellationToken = default);

        /// <summary>
        /// Calls <paramref name="rpcUrl"/>'s own <c>eth_getBalance</c> JSON-RPC method for
        /// <paramref name="address"/> and returns the native-token balance in wei, or <c>null</c> if the
        /// node is unreachable/errors - never throws.
        /// </summary>
        Task<BigInteger?> TryGetBalanceAsync(string rpcUrl, string address, CancellationToken cancellationToken = default);
    }
}
