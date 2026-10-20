using System.Buffers.Binary;
using System.Text;
using Algorand;

namespace BiatecMCP.BusinessLogic.AlgoSafe
{
    /// <summary>One decoded entry of a stored proposal payload: the <c>(txType, data)</c> tagged envelope.</summary>
    public sealed record AlgoSafePayloadEntry(ulong TxType, byte[] Data);

    /// <summary>A decoded payment payload entry (<c>PaymentTxn</c>).</summary>
    public sealed record AlgoSafePaymentPayload(Address Sender, Address Receiver, ulong Amount, ulong HasClose, Address CloseRemainderTo, string Note);

    /// <summary>A decoded asset-transfer payload entry (<c>AssetTxn</c>).</summary>
    public sealed record AlgoSafeAssetPayload(Address Sender, ulong XferAsset, Address AssetReceiver, ulong AssetAmount, ulong HasAssetClose, Address AssetCloseTo, string Note);

    /// <summary>
    /// ARC-4 codecs for the AlgoSafe payload structs - hand-written because the generated client only exposes the opaque
    /// <c>(uint64,byte[])</c> envelope, not the inner <c>PaymentTxn</c>/<c>AssetTxn</c> structs. Field order and types mirror
    /// <c>smart_contracts/shared/types.ts</c> byte for byte (<c>(address,address,uint64,uint64,address,string)</c> and
    /// <c>(address,uint64,address,uint64,uint64,address,string)</c>); cross-checked against algosdk-produced vectors in the tests.
    /// </summary>
    public static class AlgoSafePayloadCodec
    {
        private const int AddressLength = 32;

        /// <summary>The zero address - in a payload's <c>sender</c> field it means "the safe's own application account".</summary>
        public static Address ZeroAddress { get; } = new(new byte[AddressLength]);

        public static byte[] EncodePayment(Address sender, Address receiver, ulong amount, ulong hasClose, Address closeRemainderTo, string note)
        {
            var noteBytes = Encoding.UTF8.GetBytes(note ?? string.Empty);
            // head: address, address, uint64, uint64, address, uint16 offset-of-note; tail: uint16 length + note bytes.
            const int headLength = AddressLength + AddressLength + 8 + 8 + AddressLength + 2;
            var buffer = new byte[headLength + 2 + noteBytes.Length];
            var span = buffer.AsSpan();
            var offset = 0;
            offset = WriteAddress(span, offset, sender);
            offset = WriteAddress(span, offset, receiver);
            offset = WriteUInt64(span, offset, amount);
            offset = WriteUInt64(span, offset, hasClose);
            offset = WriteAddress(span, offset, closeRemainderTo);
            offset = WriteUInt16(span, offset, headLength);
            WriteUInt16(span, offset, noteBytes.Length);
            noteBytes.CopyTo(span[(headLength + 2)..]);
            return buffer;
        }

        public static byte[] EncodeAsset(Address sender, ulong xferAsset, Address assetReceiver, ulong assetAmount, ulong hasAssetClose, Address assetCloseTo, string note)
        {
            var noteBytes = Encoding.UTF8.GetBytes(note ?? string.Empty);
            const int headLength = AddressLength + 8 + AddressLength + 8 + 8 + AddressLength + 2;
            var buffer = new byte[headLength + 2 + noteBytes.Length];
            var span = buffer.AsSpan();
            var offset = 0;
            offset = WriteAddress(span, offset, sender);
            offset = WriteUInt64(span, offset, xferAsset);
            offset = WriteAddress(span, offset, assetReceiver);
            offset = WriteUInt64(span, offset, assetAmount);
            offset = WriteUInt64(span, offset, hasAssetClose);
            offset = WriteAddress(span, offset, assetCloseTo);
            offset = WriteUInt16(span, offset, headLength);
            WriteUInt16(span, offset, noteBytes.Length);
            noteBytes.CopyTo(span[(headLength + 2)..]);
            return buffer;
        }

        /// <summary>
        /// Encodes the payload argument of <c>proposeTransactionGroup</c>/<c>appendTransactionGroupPayload</c>, the ARC-4 value
        /// <c>(uint64,byte[])[]</c>. The generated client cannot be used for this argument: it concatenates the dynamic tuples without the
        /// array's per-element offsets and omits the tuple's own offset for its <c>byte[]</c> field, which the contract rejects
        /// ("invalid tail pointer"). Each element is <c>txType(8) | offset=10 (2) | length (2) | data</c>.
        /// </summary>
        public static byte[] EncodeEnvelopeArray(IReadOnlyList<AlgoSafePayloadEntry> entries)
        {
            var elements = new List<byte[]>(entries.Count);
            foreach (var entry in entries)
            {
                var element = new byte[8 + 2 + 2 + entry.Data.Length];
                BinaryPrimitives.WriteUInt64BigEndian(element, entry.TxType);
                BinaryPrimitives.WriteUInt16BigEndian(element.AsSpan(8), 10);
                BinaryPrimitives.WriteUInt16BigEndian(element.AsSpan(10), checked((ushort)entry.Data.Length));
                entry.Data.CopyTo(element, 12);
                elements.Add(element);
            }

            return EncodeDynamicArray(elements);
        }

        /// <summary>
        /// Encodes the <c>members</c> argument of <c>bootstrapGroup</c>, the ARC-4 value <c>(address,uint64,string)[]</c> - same dynamic-tuple
        /// layout as <see cref="EncodeEnvelopeArray"/> (each element: address | accountType | offset=42 | length | label).
        /// </summary>
        public static byte[] EncodeMembersArray(IReadOnlyList<(Address Address, ulong AccountType, string Label)> members)
        {
            var elements = new List<byte[]>(members.Count);
            foreach (var (address, accountType, label) in members)
            {
                var labelBytes = Encoding.UTF8.GetBytes(label ?? string.Empty);
                var element = new byte[AddressLength + 8 + 2 + 2 + labelBytes.Length];
                address.Bytes.CopyTo(element, 0);
                BinaryPrimitives.WriteUInt64BigEndian(element.AsSpan(AddressLength), accountType);
                BinaryPrimitives.WriteUInt16BigEndian(element.AsSpan(AddressLength + 8), AddressLength + 8 + 2);
                BinaryPrimitives.WriteUInt16BigEndian(element.AsSpan(AddressLength + 8 + 2), checked((ushort)labelBytes.Length));
                labelBytes.CopyTo(element, AddressLength + 8 + 2 + 2);
                elements.Add(element);
            }

            return EncodeDynamicArray(elements);
        }

        /// <summary>
        /// Encodes the <c>seed</c> argument of <c>bootstrapGroup</c>, the ARC-4 tuple <c>(string,uint64 x8)</c>: head = offset of the name (66) +
        /// eight uint64s (threshold, adminPrivileges, allowedActions, limitAssetId, dailyLimit, monthlyLimit, cooldownRounds, groupType); tail = the name.
        /// </summary>
        public static byte[] EncodeGroupSeed(string name, ulong threshold, ulong adminPrivileges, ulong allowedActions, ulong limitAssetId, ulong dailyLimit, ulong monthlyLimit, ulong cooldownRounds, ulong groupType)
        {
            var nameBytes = Encoding.UTF8.GetBytes(name ?? string.Empty);
            const int headLength = 2 + 8 * 8;
            var buffer = new byte[headLength + 2 + nameBytes.Length];
            var span = buffer.AsSpan();
            var offset = WriteUInt16(span, 0, headLength);
            foreach (var value in new[] { threshold, adminPrivileges, allowedActions, limitAssetId, dailyLimit, monthlyLimit, cooldownRounds, groupType })
            {
                offset = WriteUInt64(span, offset, value);
            }

            WriteUInt16(span, offset, nameBytes.Length);
            nameBytes.CopyTo(span[(headLength + 2)..]);
            return buffer;
        }

        /// <summary>ARC-4 dynamic array of dynamic elements: uint16 count, one uint16 offset per element (from the first byte after the count), then the elements.</summary>
        private static byte[] EncodeDynamicArray(IReadOnlyList<byte[]> elements)
        {
            var headLength = 2 * elements.Count;
            var total = 2 + headLength + elements.Sum(e => e.Length);
            var buffer = new byte[total];
            BinaryPrimitives.WriteUInt16BigEndian(buffer, checked((ushort)elements.Count));
            var offset = headLength;
            var cursor = 2 + headLength;
            for (var i = 0; i < elements.Count; i++)
            {
                BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(2 + 2 * i), checked((ushort)offset));
                elements[i].CopyTo(buffer, cursor);
                offset += elements[i].Length;
                cursor += elements[i].Length;
            }

            return buffer;
        }

        public static AlgoSafePaymentPayload DecodePayment(ReadOnlySpan<byte> data)
        {
            var offset = 0;
            var sender = ReadAddress(data, ref offset);
            var receiver = ReadAddress(data, ref offset);
            var amount = ReadUInt64(data, ref offset);
            var hasClose = ReadUInt64(data, ref offset);
            var closeTo = ReadAddress(data, ref offset);
            var note = ReadDynamicString(data, ref offset);
            return new AlgoSafePaymentPayload(sender, receiver, amount, hasClose, closeTo, note);
        }

        public static AlgoSafeAssetPayload DecodeAsset(ReadOnlySpan<byte> data)
        {
            var offset = 0;
            var sender = ReadAddress(data, ref offset);
            var xferAsset = ReadUInt64(data, ref offset);
            var receiver = ReadAddress(data, ref offset);
            var amount = ReadUInt64(data, ref offset);
            var hasClose = ReadUInt64(data, ref offset);
            var closeTo = ReadAddress(data, ref offset);
            var note = ReadDynamicString(data, ref offset);
            return new AlgoSafeAssetPayload(sender, xferAsset, receiver, amount, hasClose, closeTo, note);
        }

        /// <summary>
        /// Decodes a stored payload chunk, the ARC-4 value <c>(uint64,byte[])[]</c>: a uint16 element count, then one uint16
        /// offset per element (relative to the first byte after the count), then each element's tail - a uint64 tag, a uint16 offset
        /// to its <c>byte[]</c>, and the length-prefixed bytes.
        /// </summary>
        public static IReadOnlyList<AlgoSafePayloadEntry> DecodeEnvelopeArray(ReadOnlySpan<byte> data)
        {
            if (data.Length < 2)
            {
                throw new FormatException("Payload chunk is too short.");
            }

            var count = BinaryPrimitives.ReadUInt16BigEndian(data);
            var body = data[2..];
            var entries = new List<AlgoSafePayloadEntry>(count);
            for (var i = 0; i < count; i++)
            {
                if (body.Length < (i + 1) * 2)
                {
                    throw new FormatException("Payload chunk offsets are truncated.");
                }

                var elementOffset = BinaryPrimitives.ReadUInt16BigEndian(body[(i * 2)..]);
                if (elementOffset + 10 > body.Length)
                {
                    throw new FormatException("Payload entry is truncated.");
                }

                var element = body[elementOffset..];
                var txType = BinaryPrimitives.ReadUInt64BigEndian(element);
                var bytesOffset = BinaryPrimitives.ReadUInt16BigEndian(element[8..]);
                if (bytesOffset + 2 > element.Length)
                {
                    throw new FormatException("Payload entry bytes offset is out of range.");
                }

                var length = BinaryPrimitives.ReadUInt16BigEndian(element[bytesOffset..]);
                if (bytesOffset + 2 + length > element.Length)
                {
                    throw new FormatException("Payload entry bytes are truncated.");
                }

                entries.Add(new AlgoSafePayloadEntry(txType, element.Slice(bytesOffset + 2, length).ToArray()));
            }

            return entries;
        }

        private static int WriteAddress(Span<byte> span, int offset, Address address)
        {
            address.Bytes.CopyTo(span[offset..]);
            return offset + AddressLength;
        }

        private static int WriteUInt64(Span<byte> span, int offset, ulong value)
        {
            BinaryPrimitives.WriteUInt64BigEndian(span[offset..], value);
            return offset + 8;
        }

        private static int WriteUInt16(Span<byte> span, int offset, int value)
        {
            BinaryPrimitives.WriteUInt16BigEndian(span[offset..], checked((ushort)value));
            return offset + 2;
        }

        private static Address ReadAddress(ReadOnlySpan<byte> data, ref int offset)
        {
            if (offset + AddressLength > data.Length)
            {
                throw new FormatException("Payload entry is truncated (address).");
            }

            var address = new Address(data.Slice(offset, AddressLength).ToArray());
            offset += AddressLength;
            return address;
        }

        private static ulong ReadUInt64(ReadOnlySpan<byte> data, ref int offset)
        {
            if (offset + 8 > data.Length)
            {
                throw new FormatException("Payload entry is truncated (uint64).");
            }

            var value = BinaryPrimitives.ReadUInt64BigEndian(data[offset..]);
            offset += 8;
            return value;
        }

        /// <summary>Reads the trailing dynamic <c>string</c>: the head holds a uint16 offset into the tail, which is length-prefixed UTF-8.</summary>
        private static string ReadDynamicString(ReadOnlySpan<byte> data, ref int offset)
        {
            if (offset + 2 > data.Length)
            {
                throw new FormatException("Payload entry is truncated (string offset).");
            }

            var stringOffset = BinaryPrimitives.ReadUInt16BigEndian(data[offset..]);
            offset += 2;
            if (stringOffset + 2 > data.Length)
            {
                throw new FormatException("Payload entry string offset is out of range.");
            }

            var length = BinaryPrimitives.ReadUInt16BigEndian(data[stringOffset..]);
            if (stringOffset + 2 + length > data.Length)
            {
                throw new FormatException("Payload entry string is truncated.");
            }

            return Encoding.UTF8.GetString(data.Slice(stringOffset + 2, length));
        }
    }
}
