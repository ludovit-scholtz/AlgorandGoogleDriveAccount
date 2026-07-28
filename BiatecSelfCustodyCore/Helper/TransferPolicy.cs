namespace BiatecSelfCustodyCore.Helper
{
    /// <summary>
    /// Server-side spend ceiling / receiver allowlist checks - shared by BiatecMCP's <c>transferAsset</c>
    /// tool (F-04) and BiatecOIDC's wallet <c>/wallet/sign</c> endpoint, independent of the pure business
    /// logic of building/signing/broadcasting the transaction - kept here as small, directly
    /// unit-testable pure functions.
    /// </summary>
    public static class TransferPolicy
    {
        /// <summary><paramref name="maxAmount"/> of <c>0</c> means unbounded (no limit configured).</summary>
        public static bool ExceedsMaxAmount(ulong amount, ulong maxAmount)
        {
            return maxAmount != 0 && amount > maxAmount;
        }

        /// <summary>
        /// An empty/null <paramref name="allowedReceivers"/> means no restriction is configured. An empty
        /// <paramref name="receiverAccount"/> (self-transfer) is always allowed.
        /// </summary>
        public static bool IsReceiverAllowed(string? receiverAccount, IReadOnlyCollection<string>? allowedReceivers)
        {
            if (string.IsNullOrEmpty(receiverAccount) || allowedReceivers == null || allowedReceivers.Count == 0)
            {
                return true;
            }

            return allowedReceivers.Contains(receiverAccount, StringComparer.OrdinalIgnoreCase);
        }
    }
}
