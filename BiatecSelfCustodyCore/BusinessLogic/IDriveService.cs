using Algorand;
using Algorand.Algod.Model.Transactions;

namespace BiatecSelfCustodyCore.BusinessLogic
{
    public interface IDriveService
    {
        Task<byte[]> SignTransactionAsync(string email, byte[] txMsgPack);
        Task<string> GetAccountAddressAsync(string email);
        Task<string> GetAccessTokenAsync(string userEmail);
    }
}