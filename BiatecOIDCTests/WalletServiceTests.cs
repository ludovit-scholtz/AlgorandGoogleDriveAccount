using Algorand;
using Algorand.Algod.Model;
using Algorand.Algod.Model.Transactions;
using Algorand.Utils;
using BiatecOIDC.BusinessLogic;
using BiatecSelfCustodyCore.BusinessLogic;
using Microsoft.Extensions.Logging;
using Moq;

namespace BiatecOIDCTests
{
    [TestFixture]
    public class WalletServiceTests
    {
        private const string TestEmail = "user@example.com";
        private const string TestProvider = "Google";
        private static readonly Digest TestGenesisHash = new(new byte[32]);

        private Mock<IDriveService> _mockDriveService = null!;
        private Mock<ISpendingLimitService> _mockSpendingLimitService = null!;
        private Mock<ILogger<WalletService>> _mockLogger = null!;
        private WalletService _service = null!;

        [SetUp]
        public void SetUp()
        {
            _mockDriveService = new Mock<IDriveService>();
            _mockSpendingLimitService = new Mock<ISpendingLimitService>();
            _mockLogger = new Mock<ILogger<WalletService>>();
            _service = new WalletService(_mockDriveService.Object, _mockSpendingLimitService.Object, _mockLogger.Object);

            _mockDriveService
                .Setup(d => d.SignTransactionAsync(It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<string>(), It.IsAny<string?>()))
                .ReturnsAsync((string _, byte[] tx, string _, string? _) => tx.Reverse().ToArray());
        }

        private static byte[] BuildPayment(ulong amount)
        {
            var addr = new Account().Address;
            var pay = new PaymentTransaction
            {
                Sender = addr,
                Receiver = addr,
                Amount = amount,
                Fee = 1000,
                FirstValid = 1,
                LastValid = 1000,
                GenesisId = "testnet-v1.0",
                GenesisHash = TestGenesisHash
            };
            return Encoder.EncodeToMsgPackOrdered(pay);
        }

        private static byte[] BuildAssetTransfer(ulong assetAmount, ulong assetId = 10)
        {
            var addr = new Account().Address;
            var axfer = new AssetTransferTransaction
            {
                Sender = addr,
                AssetReceiver = addr,
                AssetAmount = assetAmount,
                XferAsset = assetId,
                Fee = 1000,
                FirstValid = 1,
                LastValid = 1000,
                GenesisId = "testnet-v1.0",
                GenesisHash = TestGenesisHash
            };
            return Encoder.EncodeToMsgPackOrdered(axfer);
        }

        private static byte[] BuildAssetCreate()
        {
            var addr = new Account().Address;
            var acfg = new AssetCreateTransaction
            {
                Sender = addr,
                Fee = 1000,
                FirstValid = 1,
                LastValid = 1000,
                GenesisId = "testnet-v1.0",
                GenesisHash = TestGenesisHash
            };
            return Encoder.EncodeToMsgPackOrdered(acfg);
        }

        [Test]
        public async Task SignTransactionGroupAsync_WithinLimit_SignsAndReturnsResult()
        {
            _mockSpendingLimitService.Setup(s => s.GetMaxAmountPerTransactionAsync(TestEmail)).ReturnsAsync(1_000_000UL);
            var tx = BuildPayment(500_000);

            var result = await _service.SignTransactionGroupAsync(TestEmail, TestProvider, new[] { tx }, "access-token");

            Assert.That(result, Has.Count.EqualTo(1));
            _mockDriveService.Verify(d => d.SignTransactionAsync(TestEmail, tx, TestProvider, "access-token"), Times.Once);
        }

        [Test]
        public void SignTransactionGroupAsync_PaymentExceedsLimit_ThrowsAndNeverSigns()
        {
            _mockSpendingLimitService.Setup(s => s.GetMaxAmountPerTransactionAsync(TestEmail)).ReturnsAsync(1_000UL);
            var tx = BuildPayment(2_000);

            var ex = Assert.ThrowsAsync<SpendingLimitExceededException>(
                async () => await _service.SignTransactionGroupAsync(TestEmail, TestProvider, new[] { tx }, null));

            Assert.That(ex!.Amount, Is.EqualTo(2_000UL));
            Assert.That(ex.MaxAmountPerTransaction, Is.EqualTo(1_000UL));
            _mockDriveService.Verify(d => d.SignTransactionAsync(It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<string>(), It.IsAny<string?>()), Times.Never);
        }

        [Test]
        public void SignTransactionGroupAsync_AssetTransferExceedsLimit_Throws()
        {
            _mockSpendingLimitService.Setup(s => s.GetMaxAmountPerTransactionAsync(TestEmail)).ReturnsAsync(100UL);
            var tx = BuildAssetTransfer(assetAmount: 500);

            Assert.ThrowsAsync<SpendingLimitExceededException>(
                async () => await _service.SignTransactionGroupAsync(TestEmail, TestProvider, new[] { tx }, null));
        }

        [Test]
        public async Task SignTransactionGroupAsync_ZeroLimitIsUnbounded_AnyAmountSigns()
        {
            _mockSpendingLimitService.Setup(s => s.GetMaxAmountPerTransactionAsync(TestEmail)).ReturnsAsync(0UL);
            var tx = BuildPayment(ulong.MaxValue / 2);

            var result = await _service.SignTransactionGroupAsync(TestEmail, TestProvider, new[] { tx }, null);

            Assert.That(result, Has.Count.EqualTo(1));
        }

        [Test]
        public async Task SignTransactionGroupAsync_NonTransferTransaction_BypassesLimitCheck()
        {
            _mockSpendingLimitService.Setup(s => s.GetMaxAmountPerTransactionAsync(TestEmail)).ReturnsAsync(1UL);
            var tx = BuildAssetCreate();

            var result = await _service.SignTransactionGroupAsync(TestEmail, TestProvider, new[] { tx }, null);

            Assert.That(result, Has.Count.EqualTo(1));
        }

        [Test]
        public void SignTransactionGroupAsync_OneOfMultipleExceedsLimit_NoneAreSigned()
        {
            _mockSpendingLimitService.Setup(s => s.GetMaxAmountPerTransactionAsync(TestEmail)).ReturnsAsync(1_000UL);
            var okTx = BuildPayment(500);
            var badTx = BuildPayment(5_000);

            Assert.ThrowsAsync<SpendingLimitExceededException>(
                async () => await _service.SignTransactionGroupAsync(TestEmail, TestProvider, new[] { okTx, badTx }, null));

            _mockDriveService.Verify(d => d.SignTransactionAsync(It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<string>(), It.IsAny<string?>()), Times.Never);
        }

        [Test]
        public async Task SignTransactionGroupAsync_MultipleValidTransactions_SignsAllInOrder()
        {
            _mockSpendingLimitService.Setup(s => s.GetMaxAmountPerTransactionAsync(TestEmail)).ReturnsAsync(10_000UL);
            var tx1 = BuildPayment(100);
            var tx2 = BuildAssetTransfer(200);

            var result = await _service.SignTransactionGroupAsync(TestEmail, TestProvider, new[] { tx1, tx2 }, null);

            Assert.That(result, Has.Count.EqualTo(2));
            Assert.That(result[0], Is.EqualTo(tx1.Reverse().ToArray()));
            Assert.That(result[1], Is.EqualTo(tx2.Reverse().ToArray()));
        }

        [Test]
        public void SignTransactionGroupAsync_EmptyGroup_Throws()
        {
            Assert.ThrowsAsync<ArgumentException>(
                async () => await _service.SignTransactionGroupAsync(TestEmail, TestProvider, Array.Empty<byte[]>(), null));
        }

        [Test]
        public void SignTransactionGroupAsync_UndecodableTransaction_ThrowsFormatException()
        {
            _mockSpendingLimitService.Setup(s => s.GetMaxAmountPerTransactionAsync(TestEmail)).ReturnsAsync(0UL);

            Assert.ThrowsAsync<FormatException>(
                async () => await _service.SignTransactionGroupAsync(TestEmail, TestProvider, new[] { new byte[] { 0xFF, 0x00 } }, null));
        }

        [Test]
        public void SignTransactionGroupAsync_EmptyEmail_Throws()
        {
            Assert.ThrowsAsync<ArgumentException>(
                async () => await _service.SignTransactionGroupAsync(string.Empty, TestProvider, new[] { BuildPayment(1) }, null));
        }
    }
}
