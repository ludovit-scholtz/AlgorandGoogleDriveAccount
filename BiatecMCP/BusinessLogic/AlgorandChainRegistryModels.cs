namespace BiatecMCP.BusinessLogic
{
    /// <summary>One entry from https://scholtz.github.io/AlgorandPublicData/genesis/genesis-list.json.</summary>
    public sealed class GenesisListEntry
    {
        public string Name { get; set; } = string.Empty;

        /// <summary>The Algorand genesis id for this chain, e.g. <c>"voimain-v1.0"</c>.</summary>
        public string Network { get; set; } = string.Empty;

        /// <summary>Aramid's numeric chain id, as a string (e.g. <c>"416101"</c>) - may be non-numeric/absent for chains Aramid doesn't bridge to.</summary>
        public string? ChainId { get; set; }

        /// <summary>Base64-encoded genesis hash - can be empty for a placeholder entry (e.g. a local sandbox), which must be skipped.</summary>
        public string GenesisHash { get; set; } = string.Empty;
    }

    /// <summary>One entry from https://scholtz.github.io/AlgorandPublicData/algod/{network}/public-algod-providers.json.</summary>
    public sealed class PublicAlgodProvider
    {
        public string ProviderName { get; set; } = string.Empty;

        /// <summary>The algod node's base URL.</summary>
        public string AlgodHost { get; set; } = string.Empty;

        /// <summary>The auth header name this node expects (e.g. <c>"X-Algo-API-Token"</c>).</summary>
        public string Header { get; set; } = string.Empty;

        /// <summary>The auth header value - often empty for a truly public node.</summary>
        public string Token { get; set; } = string.Empty;
    }

    /// <summary>
    /// A chain confirmed both listed in the public genesis registry and currently reachable via at least
    /// one of its public algod providers (whose reported genesis hash matches) - see
    /// <see cref="IAlgorandChainRegistry"/>.
    /// </summary>
    public sealed class AlgorandChain
    {
        public string GenesisId { get; set; } = string.Empty;
        public string GenesisHash { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;

        /// <summary>Aramid's numeric chain id for this chain, if the genesis list's <c>chainId</c> parsed as one - <c>null</c> if not (e.g. a chain Aramid doesn't bridge to).</summary>
        public long? AramidChainId { get; set; }

        public string AlgodApiAddress { get; set; } = string.Empty;
        public string AlgodApiToken { get; set; } = string.Empty;
        public string AlgodApiTokenHeader { get; set; } = string.Empty;
    }
}
