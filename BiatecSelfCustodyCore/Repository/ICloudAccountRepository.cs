using Algorand.Algod.Model;
using BiatecSelfCustodyCore.Model;

namespace BiatecSelfCustodyCore.Repository
{
    /// <summary>
    /// Loads (creating on first use) the AES-encrypted Algorand account file from whichever cloud
    /// storage backend the user's session is bound to.
    /// </summary>
    public interface ICloudAccountRepository
    {
        /// <summary>
        /// Loads the account for <paramref name="email"/>/<paramref name="slot"/>, creating a new
        /// encrypted account file if one doesn't exist yet.
        /// </summary>
        /// <param name="accessToken">
        /// Explicit bearer token (device-pairing path, where the token was already resolved from
        /// Redis). If omitted, the token is resolved ambiently from the current signed-in user's
        /// cookie session for <paramref name="provider"/>.
        /// </param>
        Task<Account> LoadAccountAsync(string email, int slot, StorageProvider provider, string? accessToken = null);
    }
}
