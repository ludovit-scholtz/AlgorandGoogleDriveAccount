namespace BiatecSelfCustodyCore.Model
{
    /// <summary>
    /// One independently-generated Algorand seed (mnemonic) in a user's <see cref="SeedVault"/>. Never
    /// deleted once created - a seed may still be the rekeyed-to signer on another network, or part of a
    /// multisig configured outside Biatec, long after it stops being <see cref="IsPrimary"/> here.
    /// </summary>
    public sealed class SeedVaultEntry
    {
        /// <summary>
        /// The 25-word Algorand mnemonic for this seed. Plaintext only ever exists in-memory, after the
        /// vault file has been decrypted - never logged, never returned from any API.
        /// </summary>
        public string Mnemonic { get; set; } = string.Empty;

        /// <summary>
        /// This seed's identifying address - <c>ARC76.GetEmailAccount(email, Mnemonic, slot: 0).Address</c>.
        /// Used instead of a separate opaque id, since it's already the natural, stable identifier for
        /// "this seed" from the caller's perspective.
        /// </summary>
        public string PrimaryAddress { get; set; } = string.Empty;

        /// <summary>When this seed was generated.</summary>
        public DateTimeOffset CreatedUtc { get; set; }

        /// <summary>
        /// Whether this is the seed <see cref="Repository.CloudAccountRepository.LoadAccountAsync"/> derives
        /// from for normal signing. Exactly one entry in a given <see cref="SeedVault"/> should be primary at
        /// a time - switched explicitly via <see cref="Repository.ICloudAccountRepository.SwitchPrimarySeedAsync"/>.
        /// </summary>
        public bool IsPrimary { get; set; }
    }
}
