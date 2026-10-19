using NBitcoin;
using NUnit.Framework;

namespace BiatecMCPTests
{
    /// <summary>
    /// Nethereum's <c>EthECKey.GetPrivateKeyAsBytes()</c> returns the unsigned big-endian scalar with leading zero bytes
    /// stripped, so roughly 1 in 256 keys come back shorter than 32 bytes, which <c>NBitcoin.Key</c> rejects. This
    /// reproduces that on a fixed key (the cause of an intermittent "Error loading a Bitcoin-family signing key" failure).
    /// </summary>
    [TestFixture]
    public class BitcoinKeyPaddingTests
    {
        // Private key scalar with a leading 0x00 byte (valid secp256k1 key: far below the curve order).
        private const string LeadingZeroKeyHex = "00112233445566778899aabbccddeeff00112233445566778899aabbccddeeff";

        [Test]
        public void BiatecSelfCustodyCore_ToBitcoinKey_PadsLeadingZeroBytes()
        {
            var ethKey = new Nethereum.Signer.EthECKey(LeadingZeroKeyHex);

            var key = BiatecSelfCustodyCore.Repository.CloudAccountRepository.ToBitcoinKey(ethKey);

            Assert.That(key.ToBytes(), Is.EqualTo(Convert.FromHexString(LeadingZeroKeyHex)));
        }
    }
}
