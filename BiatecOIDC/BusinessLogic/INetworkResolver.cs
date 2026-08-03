namespace BiatecOIDC.BusinessLogic
{
    /// <summary>Which blockchain "family" a resolved network belongs to.</summary>
    public enum ChainFamily
    {
        /// <summary>Algorand Virtual Machine - Algorand, Voi, Aramid, and any other AVM-compatible chain.</summary>
        Avm,

        /// <summary>Ethereum Virtual Machine - Ethereum, Gnosis, Arbitrum, Base, and any other EVM-compatible chain.</summary>
        Evm
    }

    /// <summary>
    /// One network a wallet-route <c>network</c> segment successfully resolved to. <see cref="AvmChain"/> is
    /// only set for <see cref="ChainFamily.Avm"/> - BiatecOIDC never talks to an EVM chain directly (that only
    /// happens in BiatecMCP, for balance queries), so an <see cref="ChainFamily.Evm"/> match here is purely a
    /// name recognition, letting callers return a clean "not supported yet" instead of "unknown network".
    /// </summary>
    public sealed class ResolvedNetwork
    {
        public ChainFamily Family { get; set; }
        public string DisplayName { get; set; } = string.Empty;
        public AlgorandChain? AvmChain { get; set; }
    }

    /// <summary>
    /// Resolves a friendly <c>network</c> wallet-route segment (e.g. <c>"algorand"</c>, <c>"voi"</c>,
    /// <c>"ethereum"</c>, <c>"arbitrum"</c>, or a raw Algorand genesis id) to a <see cref="ResolvedNetwork"/> -
    /// see <see cref="NetworkResolver"/>. A much lighter-weight, independent counterpart of BiatecMCP's own
    /// <c>INetworkResolver</c> (same name, unrelated types, per this repo's no-compile-time-coupling rule) -
    /// BiatecOIDC only ever needs AVM chain connection details (already available via
    /// <see cref="IAlgorandChainRegistry"/>), never a live EVM RPC.
    /// </summary>
    public interface INetworkResolver
    {
        Task<ResolvedNetwork?> ResolveAsync(string network, CancellationToken cancellationToken = default);
    }
}
