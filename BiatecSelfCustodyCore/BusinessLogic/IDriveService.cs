namespace BiatecSelfCustodyCore.BusinessLogic
{
    public interface IDriveService
    {
        Task<byte[]> SignTransactionAsync(string email, byte[] txMsgPack, string provider, string? accessToken = null);
        Task<string> GetAccountAddressAsync(string email, string provider, string? accessToken = null);
        Task<string> GetAccessTokenAsync(string userEmail);
    }
}
