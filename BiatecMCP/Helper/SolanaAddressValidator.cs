using System.Numerics;

namespace BiatecMCP.Helper
{
    /// <summary>
    /// Validates a Solana address (or blockhash) without a Solana SDK dependency: base58 (Bitcoin alphabet) that
    /// decodes to exactly 32 bytes. Used to reject an obviously-wrong recipient up front, before a blockhash is
    /// fetched or anything is signed - BiatecOIDC independently re-validates when it builds the transfer.
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
    }
}
