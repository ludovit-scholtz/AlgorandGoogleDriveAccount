using Algorand;
using Algorand.Algod.Model.Transactions;

namespace BiatecMCP.Helper
{
    /// <summary>
    /// Builds and combines Algorand multisig transactions - no key material is ever touched here. A
    /// multisig transaction is proposed once (<see cref="BuildEnvelope"/>), then independently signed by
    /// each cosigner (each producing their own fresh, fully-independent signed copy of the same envelope -
    /// via BiatecOIDC's <c>POST /wallet/sign</c>, which already branches on the <c>SignedTransaction.MSig</c>
    /// field for exactly this case), and finally combined (<see cref="Merge"/>) once enough signatures are
    /// collected. Signers don't have to share a Biatec MCP session, or even a Biatec account at all - only
    /// the same envelope bytes and, eventually, their own signed copy of it.
    /// </summary>
    public static class MultisigTransactionBuilder
    {
        /// <summary>
        /// Derives the multisig account's address from its <paramref name="version"/> (always <c>1</c> for
        /// every multisig account ever created on Algorand), <paramref name="threshold"/> (how many of
        /// <paramref name="participantAddresses"/> must sign), and participants (identified by their own
        /// normal Algorand addresses - each address already <em>is</em> that participant's ed25519 public
        /// key, base32-encoded with a checksum, so no separate "public key" input is needed).
        /// </summary>
        public static string DeriveAddress(int version, int threshold, IReadOnlyList<string> participantAddresses)
        {
            var multisigAddress = new MultisigAddress(version, threshold, ToPublicKeyBytes(participantAddresses));
            return multisigAddress.ToAddress().EncodeAsString();
        }

        /// <summary>
        /// Wraps <paramref name="innerTransactionMsgPack"/> (an unsigned transaction whose <c>Sender</c>
        /// must already be the address <see cref="DeriveAddress"/> returns for the same
        /// version/threshold/participants) into the <c>SignedTransaction</c> envelope every cosigner
        /// independently signs - an empty (<c>null</c>-signature) <c>MultisigSubsig</c> per participant, so
        /// BiatecOIDC's <c>POST /wallet/sign</c> (via <c>DriveService.SignTransactionAsync</c>) recognizes it
        /// as a multisig co-signing request rather than a plain unsigned transaction.
        /// </summary>
        public static string BuildEnvelope(byte[] innerTransactionMsgPack, int version, int threshold, IReadOnlyList<string> participantAddresses)
        {
            var inner = Algorand.Utils.Encoder.DecodeFromMsgPack<Transaction>(innerTransactionMsgPack);
            var subsigs = ToPublicKeyBytes(participantAddresses)
                .Select(pubKey => new MultisigSubsig(pubKey, null))
                .ToList();
            var envelope = new SignedTransaction
            {
                Tx = inner,
                MSig = new MultisigSignature(version, threshold, subsigs)
            };
            return Convert.ToBase64String(Algorand.Utils.Encoder.EncodeToMsgPackOrdered(envelope));
        }

        /// <summary>
        /// Combines <paramref name="signedTransactionsBase64"/> - independently-signed copies of the same
        /// multisig envelope, each contributed by one cosigner (possibly via a different Biatec MCP session
        /// or a different wallet entirely) - into one transaction with every collected signature present,
        /// via the Algorand SDK's <c>SignedTransaction.MergeMultisigTransactionBytes</c>. The result is
        /// broadcastable (via <c>executeAlgorandTransaction</c>) once at least <c>threshold</c> of the
        /// participants have signed.
        /// </summary>
        public static string Merge(IReadOnlyList<string> signedTransactionsBase64)
        {
            var rawSignedTransactions = signedTransactionsBase64
                .Select(Convert.FromBase64String)
                .ToArray();
            var merged = SignedTransaction.MergeMultisigTransactionBytes(rawSignedTransactions);
            return Convert.ToBase64String(merged);
        }

        private static List<byte[]> ToPublicKeyBytes(IReadOnlyList<string> participantAddresses) =>
            participantAddresses.Select(a => new Address(a).Bytes).ToList();
    }
}
