using BiatecSelfCustodyCore.BusinessLogic;
using BiatecSelfCustodyCore.Model;
using BiatecSelfCustodyCore.Repository;
using Microsoft.Extensions.Logging;
using Moq;
using Nethereum.Model;
using Nethereum.Signer;

namespace BiatecMCPTests
{
    /// <summary>
    /// Covers <see cref="DriveService.SignEvmTransactionAsync"/> - builds the right
    /// <c>Nethereum.Model.ISignedTransaction</c> (legacy+EIP-155 if <see cref="EvmUnsignedTransaction.GasPrice"/>
    /// is set, EIP-1559 if <see cref="EvmUnsignedTransaction.MaxFeePerGas"/>/
    /// <see cref="EvmUnsignedTransaction.MaxPriorityFeePerGas"/> are) from the seed's derived
    /// <see cref="EthECKey"/>, mirroring the existing Algorand signing path
    /// (<see cref="DriveService.SignTransactionAsync"/>) but via <c>Nethereum.Model</c>/<c>Nethereum.Signer</c>
    /// instead of the Algorand4 SDK. <see cref="ICloudAccountRepository"/> is mocked - real seed derivation
    /// is covered by <see cref="CloudAccountRepositoryTests"/>.
    /// </summary>
    [TestFixture]
    public class DriveServiceTests
    {
        private const string TestEmail = "user@example.com";
        private const string TestProvider = "Fake";
        private const string TestTo = "0x13f022d72158410433cbd66f5dd8bf6d2d0999c";

        private Mock<ICloudAccountRepository> _mockAccountRepository = null!;
        private DriveService _service = null!;
        private EthECKey _signingKey = null!;

        [SetUp]
        public void SetUp()
        {
            _signingKey = EthECKey.GenerateKey();
            _mockAccountRepository = new Mock<ICloudAccountRepository>();
            _mockAccountRepository
                .Setup(r => r.LoadEvmAccountAsync(TestEmail, It.IsAny<int>(), TestProvider, It.IsAny<string?>(), It.IsAny<string?>()))
                .ReturnsAsync(_signingKey);
            _service = new DriveService(_mockAccountRepository.Object, new Mock<ILogger<DriveService>>().Object);
        }

        private static EvmUnsignedTransaction BuildLegacyTransaction(int chainId = 1) => new()
        {
            ChainId = chainId,
            Nonce = 0,
            To = TestTo,
            Value = System.Numerics.BigInteger.Parse("1000000000000000000"),
            Data = string.Empty,
            GasLimit = 21000,
            GasPrice = System.Numerics.BigInteger.Parse("20000000000")
        };

        private static EvmUnsignedTransaction Build1559Transaction(int chainId = 1) => new()
        {
            ChainId = chainId,
            Nonce = 0,
            To = TestTo,
            Value = System.Numerics.BigInteger.Parse("1000000000000000000"),
            Data = string.Empty,
            GasLimit = 21000,
            MaxFeePerGas = System.Numerics.BigInteger.Parse("30000000000"),
            MaxPriorityFeePerGas = System.Numerics.BigInteger.Parse("1000000000")
        };

        private static string RecoverSender(byte[] signedRlp) =>
            TransactionVerificationAndRecovery.GetSenderAddress(TransactionFactory.CreateTransaction(signedRlp));

        [Test]
        public async Task SignEvmTransactionAsync_LegacyChainIdTransaction_RecoversToTheSigningKeysAddress()
        {
            var signed = await _service.SignEvmTransactionAsync(TestEmail, BuildLegacyTransaction(), TestProvider);

            Assert.That(RecoverSender(signed), Is.EqualTo(_signingKey.GetPublicAddress()).IgnoreCase);
        }

        [Test]
        public async Task SignEvmTransactionAsync_Eip1559Transaction_RecoversToTheSigningKeysAddress()
        {
            var signed = await _service.SignEvmTransactionAsync(TestEmail, Build1559Transaction(), TestProvider);

            Assert.That(RecoverSender(signed), Is.EqualTo(_signingKey.GetPublicAddress()).IgnoreCase);
        }

        [Test]
        public async Task SignEvmTransactionAsync_DifferentChainId_ProducesDifferentSignatureButSameSender()
        {
            var signedMainnet = await _service.SignEvmTransactionAsync(TestEmail, BuildLegacyTransaction(1), TestProvider);
            var signedOther = await _service.SignEvmTransactionAsync(TestEmail, BuildLegacyTransaction(137), TestProvider);

            Assert.That(signedOther, Is.Not.EqualTo(signedMainnet));
            Assert.That(RecoverSender(signedMainnet), Is.EqualTo(_signingKey.GetPublicAddress()).IgnoreCase);
            Assert.That(RecoverSender(signedOther), Is.EqualTo(_signingKey.GetPublicAddress()).IgnoreCase);
        }

        [Test]
        public void SignEvmTransactionAsync_MissingEmail_ThrowsArgumentException()
        {
            Assert.That(async () => await _service.SignEvmTransactionAsync(string.Empty, BuildLegacyTransaction(), TestProvider),
                Throws.ArgumentException);
        }

        [Test]
        public void SignEvmTransactionAsync_NullTransaction_ThrowsArgumentException()
        {
            Assert.That(async () => await _service.SignEvmTransactionAsync(TestEmail, null!, TestProvider),
                Throws.ArgumentException);
        }

        [Test]
        public async Task SignEvmTransactionAsync_ForwardsSeedAddressAndSlotToRepository()
        {
            await _service.SignEvmTransactionAsync(TestEmail, BuildLegacyTransaction(), TestProvider, "access-token", "SEED-ADDR", 4);

            _mockAccountRepository.Verify(r => r.LoadEvmAccountAsync(TestEmail, 4, TestProvider, "access-token", "SEED-ADDR"), Times.Once);
        }
    }
}
