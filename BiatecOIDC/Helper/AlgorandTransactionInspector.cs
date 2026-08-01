using MessagePack;
using MessagePack.Resolvers;

namespace BiatecOIDC.Helper
{
    /// <summary>The Algorand transaction kinds <see cref="AlgorandTransactionInspector"/> distinguishes.</summary>
    public enum AlgorandTransactionKind
    {
        /// <summary>A native ALGO payment ("pay").</summary>
        Payment,

        /// <summary>An ASA transfer ("axfer").</summary>
        AssetTransfer,

        /// <summary>Any other transaction type (application call, asset config, key registration, etc.) - not subject to spending-limit checks.</summary>
        Other
    }

    /// <summary>The transfer-relevant facts extracted from a single transaction, for spending-limit enforcement.</summary>
    /// <param name="Kind">Which kind of transaction this is.</param>
    /// <param name="Amount">
    /// For <see cref="AlgorandTransactionKind.Payment"/>, microAlgos. For
    /// <see cref="AlgorandTransactionKind.AssetTransfer"/>, base units of <paramref name="AssetId"/>.
    /// Always <c>0</c> for <see cref="AlgorandTransactionKind.Other"/>.
    /// </param>
    /// <param name="AssetId">The transferred asset id for an asset transfer; <c>0</c> otherwise (native ALGO has no asset id).</param>
    /// <param name="IsRekey">
    /// Whether this transaction carries a non-empty <c>rekey</c> field - i.e. it would permanently reassign
    /// which private key is authorized to sign for the sender's account. Independent of <paramref name="Kind"/>
    /// - a rekey can accompany a payment, an asset transfer, or any other transaction type.
    /// </param>
    public sealed record AlgorandTransferInfo(AlgorandTransactionKind Kind, ulong Amount, ulong AssetId, bool IsRekey);

    /// <summary>
    /// Determines whether a raw Algorand transaction (msgpack-encoded, as accepted by
    /// <c>WalletController.SignTransactionGroup</c>) is a payment or asset transfer, and if so, its
    /// amount - so <c>WalletService</c> can enforce the caller's spending limit before signing - and
    /// separately, whether it carries a <c>rekey</c> field, so <c>WalletController</c> can require the
    /// stricter <c>rekey</c> claim before signing a transaction that would reassign the account's
    /// authorized key.
    /// </summary>
    /// <remarks>
    /// The Algorand4 SDK's <c>Transaction</c> base class carries none of the type-specific fields (amount,
    /// receiver, asset id) - those only exist on subclasses like <c>PaymentTransaction</c>/
    /// <c>AssetTransferTransaction</c>, and each subclass's own <c>type</c> property is a hardcoded
    /// constant of that C# type, not something decoded off the wire (confirmed empirically: decoding a
    /// payment's bytes as <c>AssetTransferTransaction</c> silently reports <c>type="axfer"</c>). So the
    /// wire bytes must be peeked generically first - decoded into an untyped msgpack map - to read the
    /// real "type" discriminator, before deciding which typed subclass to trust for the amount.
    /// </remarks>
    public static class AlgorandTransactionInspector
    {
        private const string TypeKey = "type";
        private const string WrappedTransactionKey = "txn";
        private const string PaymentAmountKey = "amt";
        private const string AssetTransferAmountKey = "aamt";
        private const string AssetTransferAssetIdKey = "xaid";
        private const string PaymentType = "pay";
        private const string AssetTransferType = "axfer";
        private const string RekeyKey = "rekey";

        private static readonly MessagePackSerializerOptions MapOptions =
            MessagePackSerializerOptions.Standard.WithResolver(ContractlessStandardResolver.Instance);

        /// <summary>
        /// Inspects a single transaction's raw msgpack bytes.
        /// </summary>
        /// <param name="transactionMsgPack">
        /// Either a bare unsigned <c>Transaction</c>, or a <c>SignedTransaction</c> wrapper (e.g. a
        /// partially-signed multisig co-signing scenario) - the actual transaction fields are read from
        /// one level down (its <c>txn</c> map) in the latter case.
        /// </param>
        /// <exception cref="FormatException">
        /// The bytes are not a decodable Algorand transaction msgpack map, or have no <c>type</c> discriminator.
        /// </exception>
        public static AlgorandTransferInfo Inspect(byte[] transactionMsgPack)
        {
            if (transactionMsgPack == null || transactionMsgPack.Length == 0)
            {
                throw new FormatException("Transaction data is empty.");
            }

            var map = DecodeMap(transactionMsgPack);

            // A SignedTransaction wrapper nests the real transaction fields under "txn".
            if (!map.ContainsKey(TypeKey) && map.TryGetValue(WrappedTransactionKey, out var inner) && inner is Dictionary<object, object> innerMap)
            {
                map = innerMap;
            }

            if (!map.TryGetValue(TypeKey, out var typeObj) || typeObj is not string type)
            {
                throw new FormatException("Unable to determine the transaction's type - no 'type' field found.");
            }

            // Independent of "type" - a rekey field can accompany any transaction kind, not just pay/axfer.
            var isRekey = map.TryGetValue(RekeyKey, out var rekeyObj) && rekeyObj is byte[] { Length: > 0 };

            return type switch
            {
                PaymentType => new AlgorandTransferInfo(AlgorandTransactionKind.Payment, ReadUInt64(map, PaymentAmountKey), 0, isRekey),
                AssetTransferType => new AlgorandTransferInfo(AlgorandTransactionKind.AssetTransfer, ReadUInt64(map, AssetTransferAmountKey), ReadUInt64(map, AssetTransferAssetIdKey), isRekey),
                _ => new AlgorandTransferInfo(AlgorandTransactionKind.Other, 0, 0, isRekey)
            };
        }

        private static Dictionary<object, object> DecodeMap(byte[] transactionMsgPack)
        {
            object? decoded;
            try
            {
                decoded = MessagePackSerializer.Deserialize<object>(transactionMsgPack, MapOptions);
            }
            catch (Exception ex)
            {
                throw new FormatException("Unable to decode transaction: not valid msgpack.", ex);
            }

            if (decoded is not Dictionary<object, object> map)
            {
                throw new FormatException("Unable to decode transaction: expected a msgpack map.");
            }

            return map;
        }

        /// <summary>
        /// Algorand's canonical msgpack encoding omits keys whose value is the zero/default value, so a
        /// missing key means <c>0</c>, not an error. Msgpack also encodes integers in the smallest type
        /// that fits the value (e.g. a small amount decodes as <see cref="ushort"/>, not <see cref="ulong"/>),
        /// hence the <see cref="Convert.ToUInt64(object)"/> normalization.
        /// </summary>
        private static ulong ReadUInt64(Dictionary<object, object> map, string key)
        {
            if (!map.TryGetValue(key, out var value) || value == null)
            {
                return 0;
            }

            return Convert.ToUInt64(value);
        }
    }
}
