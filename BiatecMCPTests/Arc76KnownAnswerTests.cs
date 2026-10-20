using NUnit.Framework;
using AlgorandArc76 = ARC76Account.Algorand.ARC76;
using EthereumArc76 = ARC76Account.Ethereum.ARC76;
using SolanaArc76 = ARC76Account.Solana.ARC76;

namespace BiatecMCPTests
{
    /// <summary>
    /// Known-answer vectors for the ARC-76 derivation every user's addresses depend on. Expected values were generated with
    /// ARC76Account.* 1.0.4 (the production baseline) and verified identical on 1.0.10 when the packages were bumped - if
    /// a future ARC76Account release changes any of these, every existing user's derived addresses would change.
    /// </summary>
    [TestFixture]
    public class Arc76KnownAnswerTests
    {
        // BIP39 test-vector mnemonic - never a real wallet.
        private const string Mnemonic = "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about";

        [TestCase("alice@example.com", 0, "HBJC2ET2ZHNY4N7VWL74ZHJAXH6W4W5X5XKVBQZJ5LLUGJOUOSTBS5XSHI", "0x0d0B14da2efdb7b7aAA4b832Dd5eF95118c4BAc5")]
        [TestCase("alice@example.com", 7, "QHPNCXH56TEPMSD7RAJ4F7VCPX6T3EBQ6GGJB6SNXXGS2A3PALXZF3NUDQ", "0xc1ea605Cb2a6133b211425434E968497c6C34A73")]
        [TestCase("Bob.Smith+tag@Example.org", 0, "J4KJ3Y7XMTMVJ75QFNXGYMHNZIK2Z6GU63PK4WIMHY4CQZ5AAI5R7MXRI4", "0x6810017C97646199Fa0592D3691162bAFb1f3a2e")]
        [TestCase("Bob.Smith+tag@Example.org", 100, "NA4LIOREFA7UHROPFHCLWMCOADDKZMJZOIQUIUN6JJOZMYY63SRFR2YEKY", "0x8E8EBE51932244e8B46949686b5030eD6fBE4302")]
        public void GetEmailAccount_FixedInputs_DeriveStableAlgorandAndEvmAddresses(string email, int slot, string expectedAvm, string expectedEvm)
        {
            Assert.That(AlgorandArc76.GetEmailAccount(email, Mnemonic, slot).Address.ToString(), Is.EqualTo(expectedAvm));
            Assert.That(EthereumArc76.GetEmailAccount(email, Mnemonic, slot).GetPublicAddress(), Is.EqualTo(expectedEvm));
        }

        // Solana vectors generated with ARC76Account.Solana 1.0.4 and verified identical on 1.0.10.
        [TestCase("alice@example.com", 0, "4nrWTvCH3vGghq2FXd22mifh3rKrKAXHw8xWJxt6tZ4H")]
        [TestCase("alice@example.com", 1, "FoBNuzYQ4gpiP3jmevoiRVp3EnqBeXkB2HJ1LksSwYXJ")]
        [TestCase("alice@example.com", 7, "9jxewpDahVqWQmC5uu3gzSoKBYX8SeqVyz5QE1jan3ft")]
        [TestCase("Bob.Smith+tag@Example.org", 0, "6KhScr1kejXuRGfxJVGKcVVT4w439dLpwxq5fZD2Uq8W")]
        [TestCase("Bob.Smith+tag@Example.org", 100, "81qYtomaG7mhmpMn1cjf9eQaSmExHyXeAHisbQx976ZX")]
        public void GetEmailAccount_FixedInputs_DeriveStableSolanaAddresses(string email, int slot, string expectedSolana)
        {
            Assert.That(SolanaArc76.GetEmailAccount(email, Mnemonic, slot).PublicKey.Key, Is.EqualTo(expectedSolana));
        }
    }
}
