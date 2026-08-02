namespace BiatecOIDC.BusinessLogic
{
    /// <summary>
    /// A single signed payment/asset-transfer, recorded so <see cref="ISpendingLimitService"/> can
    /// efficiently compute the real trailing daily/weekly/monthly spend without re-querying the blockchain.
    /// Stored, AES-encrypted, as a list in the owner's own cloud drive alongside
    /// <see cref="SpendingLimitSettings"/> - never on Biatec's servers. Amounts are always recorded in USD
    /// (the Biatec Router's native valuation currency); conversion to the owner's chosen limit currency
    /// happens at read time, so historical entries stay meaningful even if the owner later switches
    /// currencies.
    /// </summary>
    public sealed class SpendingLedgerEntry
    {
        /// <summary>When this transaction was signed (UTC) - the basis for the rolling window checks.</summary>
        public DateTimeOffset TimestampUtc { get; set; }

        /// <summary>The transaction's USD value at signing time, as priced by <see cref="IAssetValuationService"/>.</summary>
        public decimal AmountUsd { get; set; }

        /// <summary>The spent asset id (<c>0</c> = native ALGO).</summary>
        public ulong AssetId { get; set; }

        /// <summary><c>"Payment"</c> or <c>"AssetTransfer"</c> - see <c>Helper.AlgorandTransactionKind</c>.</summary>
        public string Kind { get; set; } = string.Empty;

        /// <summary>
        /// The identifying (slot-0) address of the seed that signed this transaction - blank for entries
        /// recorded before per-address spending limits existed (they still count toward the global limit,
        /// just never toward any specific address-scoped one).
        /// </summary>
        public string PrimaryAddress { get; set; } = string.Empty;

        /// <summary>The ARC-76 derivation slot used within <see cref="PrimaryAddress"/>'s seed.</summary>
        public int Slot { get; set; }
    }
}
