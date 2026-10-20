using System.Buffers.Binary;
using System.Text;
using Algorand;

namespace BiatecMCP.BusinessLogic.AlgoSafe
{
    /// <summary>
    /// Builds the box names of the safe's BoxMaps: the ASCII prefix from the ARC-56 spec followed by the ARC-4 encoding of the key
    /// (a uint64 is 8 big-endian bytes, an address its 32 bytes, a struct key the concatenation of its fields).
    /// </summary>
    public static class AlgoSafeBoxNames
    {
        public static byte[] Group(ulong groupId) => Compose(AlgoSafeConstants.GroupBoxPrefix, UInt64(groupId));

        public static byte[] Member(ulong groupId, Address account) => Compose(AlgoSafeConstants.MemberBoxPrefix, UInt64(groupId), account.Bytes);

        public static byte[] Proposal(ulong proposalId) => Compose(AlgoSafeConstants.ProposalBoxPrefix, UInt64(proposalId));

        public static byte[] Approval(ulong proposalId, Address account) => Compose(AlgoSafeConstants.ApprovalBoxPrefix, UInt64(proposalId), account.Bytes);

        /// <summary>Payload chunk box: key = <c>proposalId * 7 + chunkIndex</c>.</summary>
        public static byte[] TransactionGroup(ulong proposalId, ulong chunkIndex) =>
            Compose(AlgoSafeConstants.TransactionGroupBoxPrefix, UInt64(checked(proposalId * AlgoSafeConstants.TxgKeyMultiplier + chunkIndex)));

        /// <summary>True if <paramref name="name"/> is a member box (<c>m</c> + groupId + address), and returns its parts.</summary>
        public static bool TryParseMember(byte[] name, out ulong groupId, out Address account)
        {
            groupId = 0;
            account = default!;
            if (name.Length != 1 + 8 + 32 || name[0] != (byte)AlgoSafeConstants.MemberBoxPrefix[0])
            {
                return false;
            }

            groupId = BinaryPrimitives.ReadUInt64BigEndian(name.AsSpan(1, 8));
            account = new Address(name.AsSpan(9, 32).ToArray());
            return true;
        }

        /// <summary>True if <paramref name="name"/> is an approval box (<c>a</c> + proposalId + address), and returns its parts.</summary>
        public static bool TryParseApproval(byte[] name, out ulong proposalId, out Address account)
        {
            proposalId = 0;
            account = default!;
            if (name.Length != 1 + 8 + 32 || name[0] != (byte)AlgoSafeConstants.ApprovalBoxPrefix[0])
            {
                return false;
            }

            proposalId = BinaryPrimitives.ReadUInt64BigEndian(name.AsSpan(1, 8));
            account = new Address(name.AsSpan(9, 32).ToArray());
            return true;
        }

        private static byte[] UInt64(ulong value)
        {
            var bytes = new byte[8];
            BinaryPrimitives.WriteUInt64BigEndian(bytes, value);
            return bytes;
        }

        private static byte[] Compose(string prefix, params byte[][] parts)
        {
            var prefixBytes = Encoding.ASCII.GetBytes(prefix);
            var result = new byte[prefixBytes.Length + parts.Sum(p => p.Length)];
            prefixBytes.CopyTo(result, 0);
            var offset = prefixBytes.Length;
            foreach (var part in parts)
            {
                part.CopyTo(result, offset);
                offset += part.Length;
            }

            return result;
        }
    }
}
