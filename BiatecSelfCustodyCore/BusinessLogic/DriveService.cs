using Algorand;
using Algorand.Algod.Model.Transactions;
using BiatecSelfCustodyCore.Model;
using BiatecSelfCustodyCore.Repository;
using Microsoft.Extensions.Logging;
using Nethereum.Signer;
using EvmAccessListItem = Nethereum.Model.AccessListItem;
using EvmISignedTransaction = Nethereum.Model.ISignedTransaction;
using EvmLegacyTransactionChainId = Nethereum.Model.LegacyTransactionChainId;
using EvmSignature = Nethereum.Model.Signature;
using EvmTransaction1559 = Nethereum.Model.Transaction1559;

namespace BiatecSelfCustodyCore.BusinessLogic
{
    public class DriveService : IDriveService
    {
        private readonly ICloudAccountRepository _cloudAccountRepository;
        private readonly ILogger<DriveService> _logger;

        public DriveService(
            ICloudAccountRepository cloudAccountRepository,
            ILogger<DriveService> logger)
        {
            _cloudAccountRepository = cloudAccountRepository;
            _logger = logger;
        }

        public async Task<byte[]> SignTransactionAsync(string email, byte[] txMsgPack, string provider, string? accessToken = null, string? seedAddress = null, int slot = 0)
        {
            if (string.IsNullOrEmpty(email))
            {
                throw new ArgumentException("Email is required", nameof(email));
            }

            if (txMsgPack == null || txMsgPack.Length == 0)
            {
                throw new ArgumentException("Transaction data is required", nameof(txMsgPack));
            }

            try
            {
                // Try to decode as signed transaction first (multisig scenario)
                var signedTxObj = Algorand.Utils.Encoder.DecodeFromMsgPack<SignedTransaction>(txMsgPack);
                if (signedTxObj?.MSig == null)
                {
                    throw new Exception("Signed transaction is not a multisig transaction.");
                }

                // Handle multisig transaction
                var account = await _cloudAccountRepository.LoadAccountAsync(email, slot, provider, accessToken, seedAddress);
                var address = account.Address.EncodeAsString();
                _logger?.LogInformation($"PasswordAccountSignMsig:{address}");

                var pubKeys = signedTxObj.MSig.Subsigs.Select(s => s.key).ToList();
                var msig = new MultisigAddress(
                    signedTxObj.MSig.Version,
                    signedTxObj.MSig.Threshold,
                    pubKeys);

                var signed = signedTxObj.Tx.Sign(msig, account);
                return Algorand.Utils.Encoder.EncodeToMsgPackOrdered(signed);
            }
            catch (Exception exc)
            {
                _logger?.LogDebug(exc, "Failed to decode signed transaction from MsgPack, trying as unsigned transaction.");

                try
                {
                    // Try to decode as unsigned transaction
                    var txObj = Algorand.Utils.Encoder.DecodeFromMsgPack<Transaction>(txMsgPack);
                    if (txObj == null)
                    {
                        throw new Exception("Unable to parse data as Transaction nor SignedTransaction");
                    }

                    var account = await _cloudAccountRepository.LoadAccountAsync(email, slot, provider, accessToken, seedAddress);
                    var address = account.Address.EncodeAsString();
                    _logger?.LogInformation($"PasswordAccountSign:{address}");

                    var signed = txObj.Sign(account);
                    return Algorand.Utils.Encoder.EncodeToMsgPackOrdered(signed);
                }
                catch (Exception innerExc)
                {
                    _logger?.LogError(innerExc, "Failed to sign transaction");
                    throw new Exception("Unable to parse or sign transaction data", innerExc);
                }
            }
        }

        public async Task<byte[]> SignEvmTransactionAsync(string email, EvmUnsignedTransaction transaction, string provider, string? accessToken = null, string? seedAddress = null, int slot = 0)
        {
            if (string.IsNullOrEmpty(email))
            {
                throw new ArgumentException("Email is required", nameof(email));
            }

            if (transaction == null)
            {
                throw new ArgumentException("Transaction data is required", nameof(transaction));
            }

            // Nethereum's transaction types can only be safely built from their own field-based
            // constructors - their raw-byte constructors (and TransactionFactory.CreateTransaction(byte[]))
            // decode an already-*signed* transaction (used to recover its sender), they don't accept an
            // unsigned one to sign. EIP-1559 (MaxFeePerGas/MaxPriorityFeePerGas given) signs with a 0/1
            // "y parity" byte, independent of chain id (a first-class field on the transaction itself, not
            // something the signature encodes); legacy + EIP-155 (GasPrice given) instead encodes the chain
            // id into v.
            EvmISignedTransaction signedTransaction;
            EthECDSASignature signature;
            var key = await _cloudAccountRepository.LoadEvmAccountAsync(email, slot, provider, accessToken, seedAddress);
            try
            {
                if (transaction.MaxFeePerGas.HasValue || transaction.MaxPriorityFeePerGas.HasValue)
                {
                    var typedTx = new EvmTransaction1559(
                        transaction.ChainId,
                        transaction.Nonce,
                        transaction.MaxPriorityFeePerGas ?? 0,
                        transaction.MaxFeePerGas ?? 0,
                        transaction.GasLimit,
                        transaction.To,
                        transaction.Value,
                        transaction.Data,
                        new List<EvmAccessListItem>());
                    signature = key.SignAndCalculateYParityV(typedTx.RawHash);
                    signedTransaction = typedTx;
                }
                else
                {
                    var legacyTx = new EvmLegacyTransactionChainId(
                        transaction.To,
                        transaction.Value,
                        transaction.Nonce,
                        transaction.GasPrice ?? 0,
                        transaction.GasLimit,
                        transaction.Data,
                        transaction.ChainId);
                    signature = key.SignAndCalculateV(legacyTx.RawHash, transaction.ChainId);
                    signedTransaction = legacyTx;
                }
            }
            catch (Exception exc) when (exc is not ArgumentException)
            {
                _logger?.LogDebug(exc, "Failed to build EVM transaction data.");
                throw new FormatException("Unable to parse EVM transaction data.", exc);
            }

            _logger?.LogInformation($"EvmAccountSign:{key.GetPublicAddress()}");
            signedTransaction.SetSignature(new EvmSignature(signature.R, signature.S, signature.V));
            return signedTransaction.GetRLPEncoded();
        }

        public async Task<string> GetAccountAddressAsync(string email, string provider, string? accessToken = null, string? seedAddress = null, int slot = 0)
        {
            if (string.IsNullOrEmpty(email))
            {
                throw new ArgumentException("Email is required", nameof(email));
            }

            try
            {
                var account = await _cloudAccountRepository.LoadAccountAsync(email, slot, provider, accessToken, seedAddress);
                return account.Address.EncodeAsString();
            }
            catch (Exception exc)
            {
                _logger?.LogError(exc, $"Error retrieving account address for email: {email}");
                throw;
            }
        }

        public async Task<string> GetAccessTokenAsync(string userEmail)
        {
            // This method would need to be implemented based on how you want to handle access tokens
            // For now, this is a placeholder that returns a not implemented exception
            // You might want to integrate this with your authentication system
            throw new NotImplementedException("Access token retrieval needs to be implemented based on your authentication flow");
        }
    }
}
