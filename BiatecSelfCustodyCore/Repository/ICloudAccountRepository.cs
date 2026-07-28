using Algorand.Algod.Model;

namespace BiatecSelfCustodyCore.Repository
{
    /// <summary>
    /// Loads (creating on first use) the AES-encrypted Algorand account file from whichever cloud
    /// storage provider the caller names.
    /// </summary>
    public interface ICloudAccountRepository
    {
        /// <summary>
        /// Loads the account for <paramref name="email"/>/<paramref name="slot"/>, creating a new
        /// encrypted account file if one doesn't exist yet.
        /// </summary>
        /// <param name="email"></param>
        /// <param name="slot"></param>
        /// <param name="provider">
        /// Provider name (see <c>Providers.ICloudStorageProvider.Name</c>, e.g. <c>"Google"</c>).
        /// Resolved via <c>Providers.ICloudStorageProviderCatalog</c>, which falls back to a default
        /// for an unrecognized/missing name.
        /// </param>
        /// <param name="accessToken">
        /// Explicit bearer token (device-pairing path, where the token was already resolved from
        /// Redis). If omitted, the token is resolved ambiently from the current signed-in user's
        /// cookie session for <paramref name="provider"/>.
        /// </param>
        Task<Account> LoadAccountAsync(string email, int slot, string provider, string? accessToken = null);
    }
}
