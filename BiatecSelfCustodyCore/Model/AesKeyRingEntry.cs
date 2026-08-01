namespace BiatecSelfCustodyCore.Model
{
    /// <summary>
    /// One generation of AES-256 key material in an <see cref="IAesKeyRingConfiguration"/> - identified by
    /// <see cref="KeyId"/> so a rotation can add a new generation while keeping old ones around for
    /// decrypting data that hasn't been re-encrypted yet (see <c>AesKeyRingResolver</c>).
    /// </summary>
    public sealed class AesKeyRingEntry
    {
        /// <summary>Stable identifier for this generation (e.g. a date like <c>"2026-08"</c>).</summary>
        public string KeyId { get; set; } = string.Empty;

        /// <summary>Base64-encoded 32-byte AES-256 key.</summary>
        public string Key { get; set; } = string.Empty;

        /// <summary>Base64-encoded 16-byte AES initialization vector.</summary>
        public string IV { get; set; } = string.Empty;
    }
}
