using System.Net;
using BiatecOIDC.BusinessLogic;
using BiatecOIDC.Model;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Moq.Protected;

namespace BiatecOIDCTests
{
    [TestFixture]
    public class CoinGeckoSolanaValuationServiceTests
    {
        private Mock<HttpMessageHandler> _handler = null!;
        private MemoryDistributedCache _cache = null!;
        private CoinGeckoSolanaValuationService _service = null!;

        [SetUp]
        public void SetUp()
        {
            _handler = new Mock<HttpMessageHandler>();
            _cache = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
            var config = new Mock<IOptionsMonitor<SolanaValuationConfiguration>>();
            config.Setup(c => c.CurrentValue).Returns(new SolanaValuationConfiguration());
            _service = new CoinGeckoSolanaValuationService(new HttpClient(_handler.Object), _cache, config.Object, new Mock<ILogger<CoinGeckoSolanaValuationService>>().Object);
        }

        private void Respond(HttpStatusCode status, string body) =>
            _handler.Protected()
                .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
                .ReturnsAsync(new HttpResponseMessage(status) { Content = new StringContent(body) });

        private void VerifyCalls(int times) =>
            _handler.Protected().Verify("SendAsync", Times.Exactly(times), ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>());

        [Test]
        public async Task GetUsdValueAsync_ConvertsLamportsUsingSpotPrice()
        {
            Respond(HttpStatusCode.OK, "{\"solana\":{\"usd\":150.5}}");

            var usd = await _service.GetUsdValueAsync(2_000_000_000UL); // 2 SOL

            Assert.That(usd, Is.EqualTo(301m));
        }

        [Test]
        public async Task GetUsdValueAsync_CachesThePriceAcrossCalls()
        {
            Respond(HttpStatusCode.OK, "{\"solana\":{\"usd\":100}}");

            await _service.GetUsdValueAsync(1_000_000_000UL);
            var second = await _service.GetUsdValueAsync(500_000_000UL);

            Assert.That(second, Is.EqualTo(50m));
            VerifyCalls(1);
        }

        [Test]
        public async Task GetUsdValueAsync_MissingSolanaPrice_ThrowsSolanaValuationException()
        {
            Respond(HttpStatusCode.OK, "{\"bitcoin\":{\"usd\":60000}}");

            await Assert.ThrowsAsync<SolanaValuationException>(async () => await _service.GetUsdValueAsync(1UL));
        }

        [Test]
        public async Task GetUsdValueAsync_NonPositivePrice_ThrowsSolanaValuationException()
        {
            Respond(HttpStatusCode.OK, "{\"solana\":{\"usd\":0}}");

            await Assert.ThrowsAsync<SolanaValuationException>(async () => await _service.GetUsdValueAsync(1UL));
        }

        [Test]
        public async Task GetUsdValueAsync_HttpFailure_ThrowsSolanaValuationException()
        {
            Respond(HttpStatusCode.InternalServerError, "oops");

            await Assert.ThrowsAsync<SolanaValuationException>(async () => await _service.GetUsdValueAsync(1UL));
        }

        [Test]
        public async Task GetUsdValueAsync_NullSolanaEntry_ThrowsSolanaValuationExceptionNotNullReference()
        {
            Respond(HttpStatusCode.OK, "{\"solana\":null}");

            await Assert.ThrowsAsync<SolanaValuationException>(async () => await _service.GetUsdValueAsync(1UL));
        }

        [Test]
        public async Task GetUsdValueAsync_CallerCancellation_IsNotReportedAsAValuationOutage()
        {
            _handler.Protected()
                .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
                .ThrowsAsync(new TaskCanceledException());
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await Assert.ThrowsAsync<TaskCanceledException>(async () => await _service.GetUsdValueAsync(1UL, cts.Token));
        }

        [Test]
        public async Task GetUsdValueAsync_RequestTimeout_IsReportedAsAValuationOutage()
        {
            _handler.Protected()
                .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
                .ThrowsAsync(new TaskCanceledException("timeout"));

            await Assert.ThrowsAsync<SolanaValuationException>(async () => await _service.GetUsdValueAsync(1UL));
        }
    }
}
