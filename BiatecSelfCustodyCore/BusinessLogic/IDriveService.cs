using BiatecSelfCustodyCore.Model;

namespace BiatecSelfCustodyCore.BusinessLogic
{
    public interface IDriveService
    {
        /// <param name="seedAddress">Selects which seed to sign with (<c>null</c> = the vault's current primary seed) - see <see cref="Repository.ICloudAccountRepository.LoadAccountAsync"/>.</param>
        /// <param name="slot">ARC-76 derivation index within the selected seed.</param>
        Task<byte[]> SignTransactionAsync(string email, byte[] txMsgPack, string provider, string? accessToken = null, string? seedAddress = null, int slot = 0);

        /// <summary>
        /// Signs an unsigned EVM (Ethereum-family) transaction - <paramref name="transaction"/>'s fields
        /// are used to build the right <c>Nethereum.Model.ISignedTransaction</c> (legacy if
        /// <see cref="EvmUnsignedTransaction.GasPrice"/> is set, EIP-1559 if
        /// <see cref="EvmUnsignedTransaction.MaxFeePerGas"/>/<see cref="EvmUnsignedTransaction.MaxPriorityFeePerGas"/>
        /// are) before signing it. Returns the RLP-encoded, fully-signed transaction, ready to broadcast
        /// (e.g. via <c>eth_sendRawTransaction</c>).
        /// </summary>
        /// <param name="seedAddress">Selects which seed to sign with (<c>null</c> = the vault's current primary seed).</param>
        /// <param name="slot">ARC-76 derivation index within the selected seed.</param>
        Task<byte[]> SignEvmTransactionAsync(string email, EvmUnsignedTransaction transaction, string provider, string? accessToken = null, string? seedAddress = null, int slot = 0);

        /// <param name="seedAddress">Selects which seed to read (<c>null</c> = the vault's current primary seed).</param>
        /// <param name="slot">ARC-76 derivation index within the selected seed.</param>
        Task<string> GetAccountAddressAsync(string email, string provider, string? accessToken = null, string? seedAddress = null, int slot = 0);

        /// <summary>
        /// The current primary seed's own identifying (Algorand slot-0) address - no ARC-76 derivation, just
        /// whichever vault entry is <c>IsPrimary</c> (see <see cref="Repository.ICloudAccountRepository.ResolveSeedAddressAsync"/>
        /// with a <c>null</c> selector), auto-creating a first seed if none exists yet. Used by
        /// <c>JwtIssuerService</c> to populate the <c>primary_seed_address</c> claim - a stable seed
        /// selector, not a per-chain derived address (BiatecMCP always derives the actual Algorand/EVM
        /// address for a given slot via a live <c>GET /wallet/address/{seedAddress}/{slot}</c> call instead).
        /// </summary>
        Task<string> GetPrimarySeedAddressAsync(string email, string provider, string? accessToken = null);

        Task<string> GetAccessTokenAsync(string userEmail);
    }
}
