namespace BiatecMCP.Model
{
    /// <summary>
    /// The Algorand signature schemes a seed/slot can sign with - the classic Ed25519 account, or the post-quantum Falcon-1024
    /// account (a different AVM address derived from the same seed via <c>ARC76Account.Algorand.ARC76.GetEmailFalconAccount</c>,
    /// signed by BiatecOIDC with a native <c>pqsig</c>). Mirrors BiatecOIDC's own <c>AvmKeyTypes</c> (duplicated, per this
    /// repo's no-compile-time-coupling rule) - the string values travel over the wallet API and must stay identical.
    /// </summary>
    public static class AvmKeyTypes
    {
        public const string Ed25519 = "Ed25519";
        public const string Falcon = "Falcon";

        /// <summary>
        /// Per Algorand's <c>PQSignature.Falcon1024FeeContributionFactor</c>: a Falcon-signed transaction contributes this
        /// many times the minimum fee, so transactions built for a Falcon sender get their minimum fee scaled by it.
        /// </summary>
        public const ulong FalconFeeContributionFactor = 2;

        /// <summary>
        /// Normalizes a caller-supplied key type (<c>null</c>/empty → <see cref="Ed25519"/>, case-insensitive). Throws
        /// <see cref="ArgumentException"/> for anything else, so a typo never silently falls back to the other account.
        /// </summary>
        public static string Normalize(string? keyType)
        {
            if (string.IsNullOrWhiteSpace(keyType) || string.Equals(keyType, Ed25519, StringComparison.OrdinalIgnoreCase))
            {
                return Ed25519;
            }

            if (string.Equals(keyType, Falcon, StringComparison.OrdinalIgnoreCase))
            {
                return Falcon;
            }

            throw new ArgumentException($"Unknown keyType '{keyType}'. Use '{Ed25519}' (default) or '{Falcon}' (post-quantum).", nameof(keyType));
        }

        public static bool IsFalcon(string? keyType) => Normalize(keyType) == Falcon;
    }
}
