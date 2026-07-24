namespace AlgorandGoogleDriveAccount.Model
{
    /// <summary>
    /// Optional server-side ceiling on MCP-initiated transfers (<c>McpTransferLimits</c> appsettings.json
    /// section), enforced in <c>BiatecMCPGoogle.TransferAsset</c> independent of whatever the MCP client/host
    /// does. Defaults to unbounded (no behavior change) unless an operator explicitly sets a limit.
    /// </summary>
    public class McpTransferLimitsConfiguration
    {
        /// <summary>
        /// Maximum amount (in the transferred asset's smallest unit, e.g. microAlgo) a single
        /// <c>transferAsset</c> call may move. <c>0</c> (the default) means unbounded.
        /// </summary>
        public ulong MaxAmount { get; set; } = 0;
    }
}
