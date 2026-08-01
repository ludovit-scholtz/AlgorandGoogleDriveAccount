namespace BiatecSelfCustodyCore.Model
{
    /// <summary>
    /// A rotatable AES key ring: one active generation used for all new encryption, plus zero or more
    /// historical generations kept only so data encrypted under them can still be decrypted (and then
    /// migrated onto the active generation - see <c>AesKeyRingResolver</c>/<c>EncryptedKeyRingFileStore</c>).
    /// Implemented by <see cref="AesOptions"/> and <c>BiatecOIDC.Model.ProviderTokenProtectionConfiguration</c>
    /// so both share one resolver instead of duplicating key-lookup/validation logic.
    /// </summary>
    public interface IAesKeyRingConfiguration
    {
        /// <summary>The <see cref="AesKeyRingEntry.KeyId"/> of the generation used for all new encryption.</summary>
        string ActiveKeyId { get; }

        /// <summary>Every configured generation, active and historical alike.</summary>
        List<AesKeyRingEntry> Keys { get; }
    }
}
