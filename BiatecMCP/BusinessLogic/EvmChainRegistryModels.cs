namespace BiatecMCP.BusinessLogic
{
    /// <summary>One entry from https://chainid.network/chains.json.</summary>
    public sealed class EvmChainListEntry
    {
        public string Name { get; set; } = string.Empty;
        public long ChainId { get; set; }
        public string ShortName { get; set; } = string.Empty;
        public string NativeCurrencySymbol { get; set; } = string.Empty;
        public int NativeCurrencyDecimals { get; set; } = 18;

        /// <summary>
        /// This chain's published RPC endpoints, pre-filtered at parse time to plain <c>https://</c> URLs
        /// with no <c>${...}</c> template placeholder (chains.json mixes in <c>wss://</c> and
        /// Infura/Alchemy-key-gated URLs that aren't usable without credentials this service doesn't have).
        /// </summary>
        public List<string> RpcCandidates { get; set; } = new();
    }

    /// <summary>
    /// An EVM chain confirmed reachable right now via at least one of its public RPC candidates (which
    /// reported the expected chain id) - see <see cref="IEvmChainRegistry"/>.
    /// </summary>
    public sealed class EvmChain
    {
        public long ChainId { get; set; }
        public string Name { get; set; } = string.Empty;
        public string RpcUrl { get; set; } = string.Empty;
        public string NativeCurrencySymbol { get; set; } = string.Empty;
        public int NativeCurrencyDecimals { get; set; } = 18;
    }
}
