using Algorand;
using Algorand.Algod.Model;
using Algorand.Algod.Model.Transactions;
using Algorand.Utils;
using BiatecOIDC.BusinessLogic;
using BiatecSelfCustodyCore.BusinessLogic;
using BiatecSelfCustodyCore.Repository;
using Microsoft.Extensions.Logging;
using Moq;

namespace BiatecOIDCTests
{
    [TestFixture]
    public class WalletServiceTests
    {
        private const string TestEmail = "user@example.com";
        private const string TestProvider = "Google";
        private const string ResolvedAddress = "RESOLVEDPRIMARYSEEDADDRESS";
        private static readonly Digest TestGenesisHash = new(new byte[32]);

        private Mock<IDriveService> _mockDriveService = null!;
        private Mock<ISpendingLimitService> _mockSpendingLimitService = null!;
        private Mock<IAssetValuationService> _mockValuationService = null!;
        private Mock<ICloudAccountRepository> _mockAccountRepository = null!;
        private WalletService _service = null!;

        [SetUp]
        public void SetUp()
        {
            _mockDriveService = new Mock<IDriveService>();
            _mockSpendingLimitService = new Mock<ISpendingLimitService>();
            _mockValuationService = new Mock<IAssetValuationService>();
            _mockAccountRepository = new Mock<ICloudAccountRepository>();
            _service = new WalletService(_mockDriveService.Object, _mockSpendingLimitService.Object, _mockValuationService.Object, _mockAccountRepository.Object, new Mock<ILogger<WalletService>>().Object);

            _mockAccountRepository
                .Setup(r => r.ResolveSeedAddressAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>()))
                .ReturnsAsync(ResolvedAddress);

            _mockDriveService
                .Setup(d => d.SignTransactionAsync(It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int>()))
                .ReturnsAsync((string _, byte[] tx, string _, string? _, string? _, int _) => tx.Reverse().ToArray());

            // 1 base unit == 1 USD by default, so tests can reason about amounts directly; individual
            // tests override this where they need a specific valuation.
            _mockValuationService
                .Setup(v => v.GetUsdValueAsync(It.IsAny<ulong>(), It.IsAny<ulong>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((ulong _, ulong amount, CancellationToken _) => amount);
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
        public async Task SignTransactionGroupAsync_WithinLimit_SignsAndRecordsSpend()
        {
            var tx = BuildPayment(500_000);

            var result = await _service.SignTransactionGroupAsync(TestEmail, TestProvider, new[] { tx }, "access-token");

            Assert.That(result, Has.Count.EqualTo(1));
            _mockDriveService.Verify(d => d.SignTransactionAsync(TestEmail, tx, TestProvider, "access-token", ResolvedAddress, 0), Times.Once);
            _mockSpendingLimitService.Verify(s => s.EnsureWithinLimitsAsync(TestEmail, TestProvider, "access-token", 500_000m, ResolvedAddress, 0, It.IsAny<CancellationToken>()), Times.Once);
            _mockSpendingLimitService.Verify(s => s.RecordSpendAsync(
                TestEmail, TestProvider, "access-token",
                It.Is<IReadOnlyList<SpendingLedgerEntry>>(entries =>
                    entries.Count == 1 && entries[0].AmountUsd == 500_000m && entries[0].Kind == "Payment" &&
                    entries[0].SeedAddress == ResolvedAddress && entries[0].Slot == 0),
                It.IsAny<CancellationToken>()), Times.Once);
        }

        [Test]
        public async Task SignTransactionGroupAsync_WithExplicitPrimaryAddressAndSlot_ResolvesAndForwardsThatIdentity()
        {
            const string requestedAddress = "SOME-OTHER-SEED-ADDRESS";
            _mockAccountRepository
                .Setup(r => r.ResolveSeedAddressAsync(TestEmail, TestProvider, requestedAddress, "access-token"))
                .ReturnsAsync(requestedAddress);
            var tx = BuildPayment(100);

            await _service.SignTransactionGroupAsync(TestEmail, TestProvider, new[] { tx }, "access-token", requestedAddress, 7);

            _mockAccountRepository.Verify(r => r.ResolveSeedAddressAsync(TestEmail, TestProvider, requestedAddress, "access-token"), Times.Once);
            _mockDriveService.Verify(d => d.SignTransactionAsync(TestEmail, tx, TestProvider, "access-token", requestedAddress, 7), Times.Once);
            _mockSpendingLimitService.Verify(s => s.EnsureWithinLimitsAsync(TestEmail, TestProvider, "access-token", 100m, requestedAddress, 7, It.IsAny<CancellationToken>()), Times.Once);
        }

        [Test]
        public void SignTransactionGroupAsync_UnknownPrimaryAddress_PropagatesInvalidOperationException()
        {
            _mockAccountRepository
                .Setup(r => r.ResolveSeedAddressAsync(TestEmail, TestProvider, "NOTAREALADDRESS", It.IsAny<string?>()))
                .ThrowsAsync(new InvalidOperationException("No seed with address 'NOTAREALADDRESS' exists for this account."));

            Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await _service.SignTransactionGroupAsync(TestEmail, TestProvider, new[] { BuildPayment(1) }, null, "NOTAREALADDRESS"));

            _mockDriveService.Verify(d => d.SignTransactionAsync(It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int>()), Times.Never);
        }

        [Test]
        public void SignTransactionGroupAsync_ExceedsLimit_ThrowsAndNeverSignsOrRecords()
        {
            var tx = BuildPayment(2_000);
            _mockSpendingLimitService
                .Setup(s => s.EnsureWithinLimitsAsync(TestEmail, TestProvider, It.IsAny<string?>(), 2_000m, ResolvedAddress, 0, It.IsAny<CancellationToken>()))
                .ThrowsAsync(new SpendingLimitExceededException("global-daily", 2_000m, 1_000m, "USD"));

            Assert.ThrowsAsync<SpendingLimitExceededException>(
                async () => await _service.SignTransactionGroupAsync(TestEmail, TestProvider, new[] { tx }, null));

            _mockDriveService.Verify(d => d.SignTransactionAsync(It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int>()), Times.Never);
            _mockSpendingLimitService.Verify(s => s.RecordSpendAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<IReadOnlyList<SpendingLedgerEntry>>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Test]
        public async Task SignTransactionGroupAsync_AssetTransfer_ValuesUsingItsAssetId()
        {
            var tx = BuildAssetTransfer(assetAmount: 500, assetId: 31566704);

            await _service.SignTransactionGroupAsync(TestEmail, TestProvider, new[] { tx }, null);

            _mockValuationService.Verify(v => v.GetUsdValueAsync(31566704, 500, It.IsAny<CancellationToken>()), Times.Once);
        }

        [Test]
        public void SignTransactionGroupAsync_AssetValuationFails_PropagatesAndNeverSigns()
        {
            var tx = BuildPayment(1_000);
            _mockValuationService
                .Setup(v => v.GetUsdValueAsync(It.IsAny<ulong>(), It.IsAny<ulong>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new AssetValuationException(0, new InvalidOperationException("no route")));

            Assert.ThrowsAsync<AssetValuationException>(
                async () => await _service.SignTransactionGroupAsync(TestEmail, TestProvider, new[] { tx }, null));

            _mockDriveService.Verify(d => d.SignTransactionAsync(It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int>()), Times.Never);
        }

        [Test]
        public async Task SignTransactionGroupAsync_ZeroValuedTransfer_SignsButSkipsLimitCheck()
        {
            var tx = BuildPayment(1_000);
            _mockValuationService
                .Setup(v => v.GetUsdValueAsync(It.IsAny<ulong>(), It.IsAny<ulong>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(0m);

            var result = await _service.SignTransactionGroupAsync(TestEmail, TestProvider, new[] { tx }, null);

            Assert.That(result, Has.Count.EqualTo(1));
            _mockSpendingLimitService.Verify(s => s.EnsureWithinLimitsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<decimal>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
            // Still recorded to the ledger - it's a real signed transaction, just worth $0.
            _mockSpendingLimitService.Verify(s => s.RecordSpendAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<IReadOnlyList<SpendingLedgerEntry>>(), It.IsAny<CancellationToken>()), Times.Once);
        }

        [Test]
        public async Task SignTransactionGroupAsync_NonTransferTransaction_BypassesValuationAndLimitCheck()
        {
            var tx = BuildAssetCreate();

            var result = await _service.SignTransactionGroupAsync(TestEmail, TestProvider, new[] { tx }, null);

            Assert.That(result, Has.Count.EqualTo(1));
            _mockValuationService.Verify(v => v.GetUsdValueAsync(It.IsAny<ulong>(), It.IsAny<ulong>(), It.IsAny<CancellationToken>()), Times.Never);
            _mockSpendingLimitService.Verify(s => s.EnsureWithinLimitsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<decimal>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
            _mockSpendingLimitService.Verify(s => s.RecordSpendAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<IReadOnlyList<SpendingLedgerEntry>>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Test]
        public void SignTransactionGroupAsync_GroupTotalExceedsLimit_NoneAreSigned()
        {
            var okTx = BuildPayment(500);
            var badTx = BuildPayment(5_000);
            _mockSpendingLimitService
                .Setup(s => s.EnsureWithinLimitsAsync(TestEmail, TestProvider, It.IsAny<string?>(), 5_500m, ResolvedAddress, 0, It.IsAny<CancellationToken>()))
                .ThrowsAsync(new SpendingLimitExceededException("global-daily", 5_500m, 1_000m, "USD"));

            Assert.ThrowsAsync<SpendingLimitExceededException>(
                async () => await _service.SignTransactionGroupAsync(TestEmail, TestProvider, new[] { okTx, badTx }, null));

            _mockDriveService.Verify(d => d.SignTransactionAsync(It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int>()), Times.Never);
        }

        [Test]
        public async Task SignTransactionGroupAsync_MultipleValidTransactions_SignsAllInOrder()
        {
            var tx1 = BuildPayment(100);
            var tx2 = BuildAssetTransfer(200);

            var result = await _service.SignTransactionGroupAsync(TestEmail, TestProvider, new[] { tx1, tx2 }, null);

            Assert.That(result, Has.Count.EqualTo(2));
            Assert.That(result[0], Is.EqualTo(tx1.Reverse().ToArray()));
            Assert.That(result[1], Is.EqualTo(tx2.Reverse().ToArray()));
            _mockSpendingLimitService.Verify(s => s.EnsureWithinLimitsAsync(TestEmail, TestProvider, It.IsAny<string?>(), 300m, ResolvedAddress, 0, It.IsAny<CancellationToken>()), Times.Once);
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
