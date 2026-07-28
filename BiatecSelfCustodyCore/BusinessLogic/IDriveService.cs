using Algorand;
using Algorand.Algod.Model.Transactions;
using BiatecSelfCustodyCore.Model;

namespace BiatecSelfCustodyCore.BusinessLogic
{
    public interface IDriveService
    {
        Task<byte[]> SignTransactionAsync(string email, byte[] txMsgPack, StorageProvider provider, string? accessToken = null);
        Task<string> GetAccountAddressAsync(string email, StorageProvider provider, string? accessToken = null);
        Task<string> GetAccessTokenAsync(string userEmail);
    }
}
