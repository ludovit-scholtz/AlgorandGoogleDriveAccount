namespace BiatecMCP.Model
{
    /// <summary>Algorand node settings per network, bound from the <c>Algod</c> configuration section.</summary>
    public class AlgodConfiguration
    {
        /// <summary>Algod node settings keyed by network name (e.g. "mainnet", "testnet").</summary>
        public Dictionary<string, AlgodNetworkSettings> Networks { get; set; } = new Dictionary<string, AlgodNetworkSettings>();
    }

    /// <summary>Connection details for a single Algorand network.</summary>
    public class AlgodNetworkSettings
    {
        /// <summary>Base URL of the algod REST API for this network.</summary>
        public string ApiAddress { get; set; } = string.Empty;

        /// <summary>API token used to authenticate against the algod node.</summary>
        public string ApiToken { get; set; } = string.Empty;

        /// <summary>
        /// Base URL of a block explorer, used to build links to transactions on this network - the
        /// returned transaction id is appended directly, so this must already include everything up to and
        /// including the trailing path segment/slash before it (e.g. <c>"https://lora.algokit.io/testnet/transaction/"</c>).
        /// Deliberately no default here - different chains, and even different network variants of the same
        /// chain (mainnet vs. testnet), need genuinely different explorer URLs (an Algorand mainnet
        /// explorer doesn't necessarily support testnet, and vice versa), so guessing one is worse than
        /// omitting the link entirely. Empty/unset means <c>ExecuteTransactionResponse.ExplorerLink</c> is
        /// left <c>null</c> rather than built from a wrong assumption.
        /// </summary>
        public string ExplorerBaseUrl { get; set; } = string.Empty;
    }
}
