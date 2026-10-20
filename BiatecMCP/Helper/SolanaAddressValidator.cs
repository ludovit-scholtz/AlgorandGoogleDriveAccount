using System.Numerics;

namespace BiatecMCP.Helper
{
    /// <summary>
    /// Base58 (Bitcoin alphabet) helpers for Solana without a Solana SDK dependency: validating an address (or
    /// blockhash) as a 32-byte value, and deriving the signature of a signed transaction. Used to reject an
    /// obviously-wrong recipient up front, before a blockhash is fetched or anything is signed (BiatecOIDC
    /// independently re-validates when it builds the transfer), and to tell an agent which transaction to look up
    /// when a broadcast's outcome is unknown.
    /// </summary>
    public static class SolanaAddressValidator
    {
        private const string Alphabet = "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz";

        public static bool IsValid(string? value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length is < 32 or > 44)
            {
                return false;
            }

            BigInteger number = BigInteger.Zero;
            foreach (var c in value)
            {
                var digit = Alphabet.IndexOf(c);
                if (digit < 0)
                {
                    return false;
                }

                number = number * 58 + digit;
            }

            var leadingZeros = value.TakeWhile(c => c == '1').Count();
            var bytes = number.IsZero ? Array.Empty<byte>() : number.ToByteArray(isUnsigned: true, isBigEndian: true);
            return leadingZeros + bytes.Length == 32;
        }

        /// <summary>
        /// The base58 transaction signature (what <c>sendTransaction</c> returns and explorers index by) of a signed,
        /// serialized single-signature transaction: the wire format is a compact-u16 signature count followed by the
        /// 64-byte signatures, so the first signature is bytes 1..65 when exactly one is present. Returns <c>null</c> for
        /// anything else.
        /// </summary>
        public static string? TryComputeSignature(byte[]? signedTransaction)
        {
            if (signedTransaction == null || signedTransaction.Length < 65 || signedTransaction[0] != 1)
            {
                return null;
            }

            return EncodeBase58(signedTransaction.AsSpan(1, 64));
        }

        private static string EncodeBase58(ReadOnlySpan<byte> data)
        {
            var number = new BigInteger(data, isUnsigned: true, isBigEndian: true);
            var chars = new List<char>();
            while (number > 0)
            {
                number = BigInteger.DivRem(number, 58, out var remainder);
                chars.Add(Alphabet[(int)remainder]);
            }

            foreach (var b in data)
            {
                if (b != 0)
                {
                    break;
                }

                chars.Add('1');
            }

            chars.Reverse();
            return new string(chars.ToArray());
        }
    }
}
