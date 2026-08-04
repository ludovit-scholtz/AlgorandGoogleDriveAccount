using System.Text.Json.Serialization;

namespace BiatecOIDC.BusinessLogic
{
    /// <summary>
    /// One address whose signing authority Biatec has confirmed - either trivially (a natively-derived
    /// address, at any ARC-76 slot) or, for an externally rekeyed AVM address, after an on-chain check
    /// confirming the address really is rekeyed to the given seed's key. See
    /// <see cref="IAddressActivationService"/>.
    /// </summary>
    public sealed class AddressActivationEntry
    {
        public string Address { get; set; } = string.Empty;

        /// <summary><c>"Avm"</c> or <c>"Evm"</c>.</summary>
        public string Family { get; set; } = string.Empty;

        /// <summary>Which seed's key signs for <see cref="Address"/> - its own identifying (Algorand slot-0) address.</summary>
        public string SeedAddress { get; set; } = string.Empty;

        /// <summary>ARC-76 derivation slot within that seed.</summary>
        public int Slot { get; set; }

        public DateTimeOffset ActivatedUtc { get; set; }

        /// <summary>
        /// Backward-compatibility shim for entries persisted before this property was named
        /// <c>PrimaryAddress</c> in JSON (renamed to <c>SeedAddress</c> - a plain, unattributed C# property
        /// is also its own JSON key by default, so an entry written under the old name still has JSON key
        /// <c>"primaryAddress"</c> and would otherwise silently deserialize <see cref="SeedAddress"/> to
        /// <c>string.Empty</c> - see CLAUDE.md's rename-hazard note). Unlike <c>SeedVaultEntry.SeedAddress</c>,
        /// this value has no other field it can be recomputed from, so it must be read directly rather than
        /// healed after the fact. Never itself written back out - new entries only ever set
        /// <see cref="SeedAddress"/> directly, and <see cref="JsonIgnoreCondition.WhenWritingNull"/> (the
        /// getter always returns <c>null</c>) keeps it out of newly-serialized documents.
        /// </summary>
        [JsonPropertyName("primaryAddress")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? LegacyPrimaryAddress
        {
            get => null;
            set
            {
                if (!string.IsNullOrEmpty(value) && string.IsNullOrEmpty(SeedAddress))
                {
                    SeedAddress = value;
                }
            }
        }
    }

    /// <summary>The persisted document shape - one per user, stored as <c>AddressActivations.%AESID%.dat</c>.</summary>
    public sealed class AddressActivationDocument
    {
        public List<AddressActivationEntry> Entries { get; set; } = new();
    }
}
