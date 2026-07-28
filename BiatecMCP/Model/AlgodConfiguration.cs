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

        /// <summary>Base URL of a block explorer, used to build links to transactions on this network.</summary>
        public string ExplorerBaseUrl { get; set; } = "https://allo.info/tx/";
    }
}
