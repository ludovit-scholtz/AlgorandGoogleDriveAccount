namespace BiatecSelfCustodyCore.Model
{
    /// <summary>
    /// AES-256 key/IV material used by <c>AesEncryptionHelper</c> to encrypt/decrypt the
    /// Algorand private key stored in a user's Google Drive, bound from the <c>AesOptions</c> configuration section.
    /// </summary>
    public class AesOptions
    {
        /// <summary>Base64-encoded AES key.</summary>
        public string Key { get; set; } = string.Empty;

        /// <summary>Base64-encoded AES initialization vector.</summary>
        public string IV { get; set; } = string.Empty;
    }
}
