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
    }

    /// <summary>The persisted document shape - one per user, stored as <c>AddressActivations.%AESID%.dat</c>.</summary>
    public sealed class AddressActivationDocument
    {
        public List<AddressActivationEntry> Entries { get; set; } = new();
    }
}
