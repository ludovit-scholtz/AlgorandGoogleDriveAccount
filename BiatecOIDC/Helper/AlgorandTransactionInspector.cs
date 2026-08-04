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
    /// <param name="Sender">
    /// The transaction's own <c>snd</c> field, base32-encoded - the address this transaction claims to move
    /// funds from/act as, independent of whose key material actually signs it (see
    /// <c>WalletController.SignTransactionGroup</c>'s sender-match check, which uses this to catch a caller
    /// signing under the wrong route address).
    /// </param>
    /// <param name="IsMultisig">
    /// Whether this is a <c>SignedTransaction</c> wrapper carrying a multisig envelope (an <c>msig</c> key
    /// alongside the wrapped <c>txn</c>) - see <c>WalletController.SignTransactionGroup</c>'s sender-match
    /// check, which skips that check for a multisig envelope since <see cref="Sender"/> there is the
    /// *multisig group's* address, not the individual cosigning participant's own address.
    /// </param>
    /// <param name="IsCloseOut">
    /// Whether this transaction carries a non-empty <c>close</c> (payment) or <c>aclose</c> (asset transfer)
    /// field - i.e. it would sweep the sender's *entire remaining* ALGO balance, or entire remaining holding
    /// of the transferred asset, to the named address, on top of (or instead of) <see cref="Amount"/>. The
    /// swept amount is not knowable from the transaction bytes alone (it depends on the account's live
    /// balance at execution time), so unlike a payment/asset-transfer's own <see cref="Amount"/> it can never
    /// be priced - see <c>WalletController.SignTransactionGroup</c>, which rejects any such transaction
    /// outright rather than pricing it at whatever <see cref="Amount"/> happens to be (audit finding H-01/R-024).
    /// </param>
    public sealed record AlgorandTransferInfo(AlgorandTransactionKind Kind, ulong Amount, ulong AssetId, bool IsRekey, string Sender, bool IsMultisig, bool IsCloseOut);

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
        private const string SenderKey = "snd";
        private const string MultisigKey = "msig";
        private const string PaymentCloseKey = "close";
        private const string AssetTransferCloseKey = "aclose";

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

            // A SignedTransaction wrapper nests the real transaction fields under "txn"; an "msig" key
            // alongside it means this is specifically a multisig envelope.
            Dictionary<object, object>? innerMap = null;
            if (!map.ContainsKey(TypeKey) && map.TryGetValue(WrappedTransactionKey, out var inner) && inner is Dictionary<object, object> unwrapped)
            {
                innerMap = unwrapped;
            }

            var isMultisig = innerMap != null && map.ContainsKey(MultisigKey);
            if (innerMap != null)
            {
                map = innerMap;
            }

            if (!map.TryGetValue(TypeKey, out var typeObj) || typeObj is not string type)
            {
                throw new FormatException("Unable to determine the transaction's type - no 'type' field found.");
            }

            // Independent of "type" - a rekey field can accompany any transaction kind, not just pay/axfer.
            var isRekey = map.TryGetValue(RekeyKey, out var rekeyObj) && rekeyObj is byte[] { Length: > 0 };
            var sender = ReadAddress(map, SenderKey);

            // "close"/"aclose" sweep the sender's *entire remaining* balance/holding to the named address,
            // independent of (and possibly in addition to) "amt"/"aamt" - see AlgorandTransferInfo.IsCloseOut's
            // remarks. Read generically (any non-empty 32-byte address field), not conditioned on "type", for
            // the same defense-in-depth reason "rekey" is read unconditionally: a future transaction type
            // could add a similarly-shaped field this code doesn't yet know the name of, and a caller-supplied
            // payload should never be trusted to only carry the fields its own "type" nominally implies.
            var isCloseOut = HasNonEmptyAddress(map, PaymentCloseKey) || HasNonEmptyAddress(map, AssetTransferCloseKey);

            return type switch
            {
                PaymentType => new AlgorandTransferInfo(AlgorandTransactionKind.Payment, ReadUInt64(map, PaymentAmountKey), 0, isRekey, sender, isMultisig, isCloseOut),
                AssetTransferType => new AlgorandTransferInfo(AlgorandTransactionKind.AssetTransfer, ReadUInt64(map, AssetTransferAmountKey), ReadUInt64(map, AssetTransferAssetIdKey), isRekey, sender, isMultisig, isCloseOut),
                _ => new AlgorandTransferInfo(AlgorandTransactionKind.Other, 0, 0, isRekey, sender, isMultisig, isCloseOut)
            };
        }

        private static bool HasNonEmptyAddress(Dictionary<object, object> map, string key) =>
            map.TryGetValue(key, out var value) && value is byte[] { Length: > 0 };

        /// <summary>Reads a 32-byte address field and base32-encodes it, or <c>""</c> if the key is absent.</summary>
        private static string ReadAddress(Dictionary<object, object> map, string key)
        {
            if (!map.TryGetValue(key, out var value) || value is not byte[] { Length: 32 } bytes)
            {
                return string.Empty;
            }

            return new Algorand.Address(bytes).EncodeAsString();
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
