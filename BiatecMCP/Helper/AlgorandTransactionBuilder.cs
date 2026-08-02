using System.Text;
using Algorand;
using Algorand.Algod.Model;
using Algorand.Algod.Model.Transactions;

namespace BiatecMCP.Helper
{
    /// <summary>
    /// Builds unsigned Algorand transactions and encodes them to the canonical msgpack bytes
    /// <c>POST /wallet/sign</c> (BiatecOIDC) expects - no key material is ever touched here, since this
    /// service no longer holds any (see <c>CLAUDE.md</c>'s BiatecMCP architecture notes). The suggested
    /// transaction parameters (fee, valid round range, genesis hash/id) must be fetched from Algod by the
    /// caller and passed in.
    /// </summary>
    public static class AlgorandTransactionBuilder
    {
        /// <summary>Builds an unsigned native-ALGO payment transaction, canonical-msgpack-encoded.</summary>
        public static byte[] BuildPayment(Address sender, Address receiver, ulong microAlgos, string? note, TransactionParametersResponse suggestedParams)
        {
            var transaction = PaymentTransaction.GetPaymentTransactionFromNetworkTransactionParameters(sender, receiver, microAlgos, note ?? string.Empty, suggestedParams);
            return Algorand.Utils.Encoder.EncodeToMsgPackOrdered(transaction);
        }

        /// <summary>Builds an unsigned ASA transfer transaction, canonical-msgpack-encoded.</summary>
        public static byte[] BuildAssetTransfer(Address sender, Address receiver, ulong assetId, ulong amount, string? note, TransactionParametersResponse suggestedParams)
        {
            var transaction = new AssetTransferTransaction
            {
                Sender = sender,
                AssetReceiver = receiver,
                AssetAmount = amount,
                XferAsset = assetId,
                Note = string.IsNullOrEmpty(note) ? null : Encoding.UTF8.GetBytes(note)
            };
            transaction.FillInParams(suggestedParams);
            return Algorand.Utils.Encoder.EncodeToMsgPackOrdered(transaction);
        }

        /// <summary>
        /// Builds an unsigned asset opt-in transaction, canonical-msgpack-encoded - the standard Algorand
        /// pattern of a zero-amount asset transfer to oneself.
        /// </summary>
        public static byte[] BuildOptIn(Address sender, ulong assetId, string? note, TransactionParametersResponse suggestedParams) =>
            BuildAssetTransfer(sender, sender, assetId, amount: 0, note, suggestedParams);

        /// <summary>
        /// Builds an unsigned ASA creation transaction, canonical-msgpack-encoded. <paramref name="manager"/>/
        /// <paramref name="reserve"/>/<paramref name="freeze"/>/<paramref name="clawback"/> each default to
        /// <paramref name="sender"/> when not given - the common "creator keeps every role" case; pass an
        /// empty/zero address explicitly to permanently renounce a role instead.
        /// </summary>
        public static byte[] BuildAssetCreate(
            Address sender,
            ulong total,
            ulong decimals,
            string? unitName,
            string? assetName,
            string? url,
            bool defaultFrozen,
            TransactionParametersResponse suggestedParams,
            Address? manager = null,
            Address? reserve = null,
            Address? freeze = null,
            Address? clawback = null)
        {
            var transaction = new AssetCreateTransaction
            {
                Sender = sender,
                AssetParams = new AssetParams
                {
                    Total = total,
                    Decimals = decimals,
                    UnitName = unitName,
                    Name = assetName,
                    Url = url,
                    DefaultFrozen = defaultFrozen,
                    Manager = manager ?? sender,
                    Reserve = reserve ?? sender,
                    Freeze = freeze ?? sender,
                    Clawback = clawback ?? sender
                }
            };
            transaction.FillInParams(suggestedParams);
            return Algorand.Utils.Encoder.EncodeToMsgPackOrdered(transaction);
        }
    }
}
