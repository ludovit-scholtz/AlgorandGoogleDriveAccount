using BiatecOIDC.BusinessLogic;
using BiatecOIDC.Model;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace BiatecOIDCTests
{
    [TestFixture]
    public class BiatecRouterValuationServiceTests
    {
        private const ulong UsdcAssetId = 31566704;

        private Mock<IBiatecRouterQuoteClient> _mockRouterClient = null!;
        private BiatecRouterValuationService _service = null!;

        [SetUp]
        public void SetUp()
        {
            _mockRouterClient = new Mock<IBiatecRouterQuoteClient>();

            var config = new Mock<IOptionsMonitor<SpendingLimitsConfiguration>>();
            config.Setup(c => c.CurrentValue).Returns(new SpendingLimitsConfiguration
            {
                UsdReferenceAssetId = UsdcAssetId,
                UsdReferenceAssetDecimals = 6
            });

            _service = new BiatecRouterValuationService(_mockRouterClient.Object, config.Object, new Mock<ILogger<BiatecRouterValuationService>>().Object);
        }

        [Test]
        public async Task GetUsdValueAsync_ZeroAmount_ReturnsZeroWithoutCallingRouter()
        {
            var result = await _service.GetUsdValueAsync(assetId: 0, amountBaseUnits: 0);

            Assert.That(result, Is.EqualTo(0m));
            _mockRouterClient.Verify(r => r.QuoteAsync(It.IsAny<long>(), It.IsAny<long>(), It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Test]
        public async Task GetUsdValueAsync_ReferenceAssetItself_ConvertsLocallyWithoutCallingRouter()
        {
            // 5 USDC (6 decimals) spent directly - 1:1 with USD, no route needed.
            var result = await _service.GetUsdValueAsync(assetId: UsdcAssetId, amountBaseUnits: 5_000_000);

            Assert.That(result, Is.EqualTo(5m));
            _mockRouterClient.Verify(r => r.QuoteAsync(It.IsAny<long>(), It.IsAny<long>(), It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Test]
        public async Task GetUsdValueAsync_OtherAsset_QuotesAgainstUsdcAndConvertsDecimals()
        {
            // 1 ALGO (asset id 0) quoted for 0.2 USDC (200,000 base units at 6 decimals).
            _mockRouterClient
                .Setup(r => r.QuoteAsync(0, (long)UsdcAssetId, 1_000_000, It.IsAny<CancellationToken>()))
                .ReturnsAsync(200_000);

            var result = await _service.GetUsdValueAsync(assetId: 0, amountBaseUnits: 1_000_000);

            Assert.That(result, Is.EqualTo(0.2m));
        }

        [Test]
        public void GetUsdValueAsync_RouterThrows_WrapsInAssetValuationException()
        {
            _mockRouterClient
                .Setup(r => r.QuoteAsync(It.IsAny<long>(), It.IsAny<long>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new HttpRequestException("router unreachable"));

            var ex = Assert.ThrowsAsync<AssetValuationException>(async () => await _service.GetUsdValueAsync(assetId: 999, amountBaseUnits: 1));

            Assert.That(ex!.AssetId, Is.EqualTo(999UL));
            Assert.That(ex.InnerException, Is.InstanceOf<HttpRequestException>());
        }
    }
}
