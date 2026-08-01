namespace BiatecSelfCustodyCore.Model
{
    /// <summary>
    /// AES-256 key ring used by <c>AesEncryptionHelper</c>/<c>EncryptedKeyRingFileStore</c> to encrypt/decrypt
    /// the Algorand private key stored in a user's Google Drive/OneDrive, bound from the <c>AesOptions</c>
    /// configuration section. A rotatable key ring rather than a single key/IV pair - see
    /// <see cref="IAesKeyRingConfiguration"/> for the rotation model.
    /// </summary>
    public class AesOptions : IAesKeyRingConfiguration
    {
        /// <inheritdoc />
        public string ActiveKeyId { get; set; } = string.Empty;

        /// <inheritdoc />
        public List<AesKeyRingEntry> Keys { get; set; } = new();
    }
}
