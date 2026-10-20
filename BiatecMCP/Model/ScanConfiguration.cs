namespace BiatecMCP.Model
{
    /// <summary>Bound from <c>BiatecScan</c> - where BiatecMCP reads Biatec Scan (AVMTradeReporter) pool/asset data from.</summary>
    public class ScanConfiguration
    {
        /// <summary>The ARC-14 realm Biatec Scan's API expects in the authentication transaction's note.</summary>
        public string Realm { get; set; } = "BiatecScan#ARC14";

        /// <summary>
        /// API base URL per BiatecMCP network code (the exact codes from <c>listSupportedNetworks</c>). A network without an entry has no
        /// Biatec Scan deployment and the Scan tools refuse it with the list of configured ones.
        /// </summary>
        public Dictionary<string, string> Networks { get; set; } = new(StringComparer.OrdinalIgnoreCase)
        {
            ["algorand-mainnet"] = "https://api.algorand.scan.biatec.io",
            ["algorand-testnet"] = "https://api.testnet.scan.biatec.io",
            ["voi-mainnet"] = "https://api.voi.scan.biatec.io"
        };
    }
}
