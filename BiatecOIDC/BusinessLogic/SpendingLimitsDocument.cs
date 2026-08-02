namespace BiatecOIDC.BusinessLogic
{
    /// <summary>
    /// The shape actually persisted (AES-encrypted) for a wallet owner's spending limits - a single
    /// <see cref="Global"/> bucket (checked against every signed transaction, regardless of which address
    /// signed it - the pre-multi-address behavior, unchanged), plus zero or more <see cref="PerAddress"/>
    /// buckets scoped to one <c>(primaryAddress, slot)</c> signing identity each. Both tiers are enforced
    /// together by <see cref="SpendingLimitService.EnsureWithinLimitsAsync"/> whenever a specific address
    /// signs - a transaction is blocked if it would exceed either.
    /// </summary>
    public sealed class SpendingLimitsDocument
    {
        /// <summary>The account-wide limit, counted against every signed payment/asset-transfer regardless of which address signed it.</summary>
        public SpendingLimitSettings Global { get; set; } = new();

        /// <summary>
        /// Per-signing-identity limits, keyed by <c>SpendingLimitService.BuildAddressKey(primaryAddress, slot)</c>.
        /// A key with no entry here has no address-specific limit configured (only <see cref="Global"/> applies).
        /// </summary>
        public Dictionary<string, SpendingLimitSettings> PerAddress { get; set; } = new();
    }
}
