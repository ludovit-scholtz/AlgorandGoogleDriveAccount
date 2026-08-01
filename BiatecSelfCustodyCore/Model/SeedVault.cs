namespace BiatecSelfCustodyCore.Model
{
    /// <summary>
    /// The full set of Algorand seeds a user has ever had Biatec generate for them, stored as the encrypted
    /// content of the self-custody account file (<c>Configuration.StorageFileName</c>, AES key ring per
    /// <c>AesOptions</c>). See <see cref="SeedVaultEntry"/> for why entries are never removed.
    /// </summary>
    public sealed class SeedVault
    {
        public List<SeedVaultEntry> Seeds { get; set; } = new();
    }
}
