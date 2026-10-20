using Algorand;
using Algorand.Algod.Model;
using Algorand.Algod.Model.Transactions;
using BiatecSelfCustodyCore.BusinessLogic;
using BiatecSelfCustodyCore.Repository;
using Microsoft.Extensions.Logging;
using Moq;
using AlgorandArc76 = ARC76Account.Algorand.ARC76;

namespace BiatecMCPTests
{
    /// <summary>
    /// Covers <see cref="DriveService.SignFalconTransactionAsync"/> - signs an unsigned Algorand transaction with the seed/slot's
    /// post-quantum Falcon-1024 account (native <c>pqsig</c>), using a real ARC-76 Falcon derivation so the produced signature
    /// is verified end to end. <see cref="ICloudAccountRepository"/> is mocked.
    /// </summary>
    [TestFixture]
    public class FalconDriveServiceTests
    {
        private const string TestEmail = "user@example.com";
        private const string TestProvider = "Fake";
        // BIP39 test-vector mnemonic - never a real wallet.
        private const string Mnemonic = "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about";

        private Mock<ICloudAccountRepository> _mockAccountRepository = null!;
        private DriveService _service = null!;
        private Address _falconAddress = null!;

        [SetUp]
        public void SetUp()
        {
            using (var probe = AlgorandArc76.GetEmailFalconAccount(TestEmail, Mnemonic, 0))
            {
                _falconAddress = probe.Address;
            }

            _mockAccountRepository = new Mock<ICloudAccountRepository>();
            // A fresh instance per call - DriveService disposes the account (zeroing its private key) after signing.
            _mockAccountRepository
                .Setup(r => r.LoadFalconAccountAsync(TestEmail, It.IsAny<int>(), TestProvider, It.IsAny<string?>(), It.IsAny<string?>()))
                .ReturnsAsync(() => AlgorandArc76.GetEmailFalconAccount(TestEmail, Mnemonic, 0));
            _service = new DriveService(_mockAccountRepository.Object, new Mock<ILogger<DriveService>>().Object);
        }

        private static PaymentTransaction BuildPayment(Address sender) => new()
        {
            Sender = sender,
            Receiver = sender,
            Amount = 1,
            Fee = 2000,
            FirstValid = 1,
            LastValid = 1000,
            GenesisId = "testnet-v1.0",
            GenesisHash = new Digest("SGO1GKSzyE7IEPItTxCByw9x8FmnrCDexi9/cOUJOiI=")
        };

        [Test]
        public async Task SignFalconTransactionAsync_OwnSender_ProducesVerifiablePqSignatureWithoutAuthAddr()
        {
            var tx = BuildPayment(_falconAddress);

            var signedBytes = await _service.SignFalconTransactionAsync(TestEmail, Algorand.Utils.Encoder.EncodeToMsgPackOrdered(tx), TestProvider);

            var signed = Algorand.Utils.Encoder.DecodeFromMsgPack<SignedTransaction>(signedBytes);
            Assert.That(signed.PQSig, Is.Not.Null);
            Assert.That(signed.Sig, Is.Null);
            Assert.That(signed.AuthAddr, Is.Null);
            Assert.That(signed.PQSig.Address.EncodeAsString(), Is.EqualTo(_falconAddress.EncodeAsString()));
            Assert.That(signed.PQSig.Verify(signed.Tx.BytesToSign()), Is.True);
        }

        [Test]
        public async Task SignFalconTransactionAsync_RekeyedSender_SetsAuthAddrToFalconAddress()
        {
            var external = new Account().Address;
            var tx = BuildPayment(external);

            var signedBytes = await _service.SignFalconTransactionAsync(TestEmail, Algorand.Utils.Encoder.EncodeToMsgPackOrdered(tx), TestProvider);

            var signed = Algorand.Utils.Encoder.DecodeFromMsgPack<SignedTransaction>(signedBytes);
            Assert.That(signed.AuthAddr.EncodeAsString(), Is.EqualTo(_falconAddress.EncodeAsString()));
        }

        [Test]
        public async Task SignFalconTransactionAsync_Garbage_ThrowsFormatException()
        {
            await Assert.ThrowsAsync<FormatException>(async () => await _service.SignFalconTransactionAsync(TestEmail, new byte[] { 1, 2, 3 }, TestProvider));
        }

        [Test]
        public async Task SignFalconTransactionAsync_ForwardsSeedAddressAndSlotToRepository()
        {
            await _service.SignFalconTransactionAsync(TestEmail, Algorand.Utils.Encoder.EncodeToMsgPackOrdered(BuildPayment(_falconAddress)), TestProvider, "tok", "SEED", 4);

            _mockAccountRepository.Verify(r => r.LoadFalconAccountAsync(TestEmail, 4, TestProvider, "tok", "SEED"), Times.Once);
            _mockAccountRepository.Verify(r => r.LoadAccountAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>()), Times.Never);
        }

        [Test]
        public void FalconDerivation_IsDeterministicAndDistinctFromEd25519AndAcrossSlots()
        {
            using var a = AlgorandArc76.GetEmailFalconAccount(TestEmail, Mnemonic, 0);
            using var b = AlgorandArc76.GetEmailFalconAccount(TestEmail, Mnemonic, 0);
            using var slot1 = AlgorandArc76.GetEmailFalconAccount(TestEmail, Mnemonic, 1);
            var ed = AlgorandArc76.GetEmailAccount(TestEmail, Mnemonic, 0);

            Assert.That(a.Address.EncodeAsString(), Is.EqualTo(b.Address.EncodeAsString()));
            Assert.That(a.Address.EncodeAsString(), Is.Not.EqualTo(slot1.Address.EncodeAsString()));
            Assert.That(a.Address.EncodeAsString(), Is.Not.EqualTo(ed.Address.EncodeAsString()));
        }
    }
}
