namespace BiatecSelfCustodyCore.BusinessLogic
{
    public interface IDriveService
    {
        /// <param name="primaryAddress">Selects which seed to sign with (<c>null</c> = the vault's current primary seed) - see <see cref="Repository.ICloudAccountRepository.LoadAccountAsync"/>.</param>
        /// <param name="slot">ARC-76 derivation index within the selected seed.</param>
        Task<byte[]> SignTransactionAsync(string email, byte[] txMsgPack, string provider, string? accessToken = null, string? primaryAddress = null, int slot = 0);

        /// <param name="primaryAddress">Selects which seed to read (<c>null</c> = the vault's current primary seed).</param>
        /// <param name="slot">ARC-76 derivation index within the selected seed.</param>
        Task<string> GetAccountAddressAsync(string email, string provider, string? accessToken = null, string? primaryAddress = null, int slot = 0);
        Task<string> GetAccessTokenAsync(string userEmail);
    }
}
