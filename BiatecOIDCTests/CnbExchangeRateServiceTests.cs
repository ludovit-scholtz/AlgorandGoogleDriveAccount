using System.Net;
using BiatecOIDC.BusinessLogic;
using BiatecOIDC.Model;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Moq.Protected;

namespace BiatecOIDCTests
{
    [TestFixture]
    public class CnbExchangeRateServiceTests
    {
        private const string SampleFeed = """
            {
              "rates": [
                { "country": "USA", "currency": "dollar", "amount": 1, "currencyCode": "USD", "rate": 22.705 },
                { "country": "EMU", "currency": "euro", "amount": 1, "currencyCode": "EUR", "rate": 24.5 },
                { "country": "Japan", "currency": "yen", "amount": 100, "currencyCode": "JPY", "rate": 15.0 }
              ]
            }
            """;

        private Mock<HttpMessageHandler> _mockHandler = null!;
        private InMemoryDistributedCache _cache = null!;
        private CnbExchangeRateService _service = null!;

        [SetUp]
        public void SetUp()
        {
            _mockHandler = new Mock<HttpMessageHandler>();
            SetupFeedResponse(SampleFeed);

            var httpClient = new HttpClient(_mockHandler.Object);
            _cache = new InMemoryDistributedCache();

            var config = new Mock<IOptionsMonitor<ExchangeRateConfiguration>>();
            config.Setup(c => c.CurrentValue).Returns(new ExchangeRateConfiguration());

            _service = new CnbExchangeRateService(httpClient, _cache, config.Object, new Mock<ILogger<CnbExchangeRateService>>().Object);
        }

        private void SetupFeedResponse(string json)
        {
            _mockHandler.Protected()
                .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
                .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
        }

        private void VerifyHttpCallCount(int times)
        {
            _mockHandler.Protected().Verify("SendAsync", Times.Exactly(times), ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>());
        }

        [Test]
        public async Task GetSupportedCurrenciesAsync_IncludesUsdCzkAndFeedCurrencies()
        {
            var currencies = await _service.GetSupportedCurrenciesAsync();

            var codes = currencies.Select(c => c.Code).ToList();
            Assert.That(codes, Does.Contain("USD"));
            Assert.That(codes, Does.Contain("CZK"));
            Assert.That(codes, Does.Contain("EUR"));
            Assert.That(codes, Does.Contain("JPY"));

            var usd = currencies.Single(c => c.Code == "USD");
            Assert.That(usd.UsdPerUnit, Is.EqualTo(1m));
        }

        [Test]
        public async Task GetSupportedCurrenciesAsync_JpyRate_AccountsForNonOneAmount()
        {
            // 100 JPY = 15.0 CZK -> 1 JPY = 0.15 CZK. 1 USD = 22.705 CZK. UsdPerUnit(JPY) = 0.15 / 22.705.
            var currencies = await _service.GetSupportedCurrenciesAsync();
            var jpy = currencies.Single(c => c.Code == "JPY");

            var expected = 0.15m / 22.705m;
            Assert.That(jpy.UsdPerUnit, Is.EqualTo(expected).Within(0.0000001m));
        }

        [Test]
        public async Task ConvertFromUsdAsync_UsdTarget_ReturnsSameAmountWithoutHttpCall()
        {
            var result = await _service.ConvertFromUsdAsync(100m, "USD");

            Assert.That(result, Is.EqualTo(100m));
            VerifyHttpCallCount(0);
        }

        [Test]
        public async Task ConvertFromUsdAsync_EurTarget_ConvertsViaCzkPivot()
        {
            var result = await _service.ConvertFromUsdAsync(100m, "eur");

            // usdPerUnit(EUR) = 24.5 / 22.705; amountEur = 100 / usdPerUnit(EUR) = 100 * 22.705 / 24.5
            var expected = 100m * 22.705m / 24.5m;
            Assert.That(result, Is.EqualTo(expected).Within(0.0001m));
        }

        [Test]
        public async Task ConvertFromUsdAsync_CzkTarget_ConvertsToCzk()
        {
            var result = await _service.ConvertFromUsdAsync(1m, "CZK");

            Assert.That(result, Is.EqualTo(22.705m).Within(0.0001m));
        }

        [Test]
        public void ConvertFromUsdAsync_UnsupportedCurrency_Throws()
        {
            var ex = Assert.ThrowsAsync<UnsupportedCurrencyException>(async () => await _service.ConvertFromUsdAsync(1m, "XYZ"));
            Assert.That(ex!.CurrencyCode, Is.EqualTo("XYZ"));
        }

        [Test]
        public async Task IsSupportedCurrencyAsync_KnownAndUnknownCodes()
        {
            Assert.That(await _service.IsSupportedCurrencyAsync("USD"), Is.True);
            Assert.That(await _service.IsSupportedCurrencyAsync("czk"), Is.True);
            Assert.That(await _service.IsSupportedCurrencyAsync("eur"), Is.True);
            Assert.That(await _service.IsSupportedCurrencyAsync("XYZ"), Is.False);
            Assert.That(await _service.IsSupportedCurrencyAsync(null), Is.False);
            Assert.That(await _service.IsSupportedCurrencyAsync("  "), Is.False);
        }

        [Test]
        public async Task RateTable_IsCachedAcrossCalls_OnlyFetchesOnce()
        {
            await _service.GetSupportedCurrenciesAsync();
            await _service.ConvertFromUsdAsync(1m, "EUR");
            await _service.IsSupportedCurrencyAsync("JPY");

            VerifyHttpCallCount(1);
        }

        [Test]
        public void FeedMissingUsdRate_ThrowsInvalidOperationException()
        {
            SetupFeedResponse("""{ "rates": [ { "country": "EMU", "currency": "euro", "amount": 1, "currencyCode": "EUR", "rate": 24.5 } ] }""");

            Assert.ThrowsAsync<InvalidOperationException>(async () => await _service.GetSupportedCurrenciesAsync());
        }

        private sealed class InMemoryDistributedCache : IDistributedCache
        {
            private readonly Dictionary<string, byte[]> _values = new(StringComparer.Ordinal);

            public byte[]? Get(string key)
            {
                _values.TryGetValue(key, out var value);
                return value;
            }

            public Task<byte[]?> GetAsync(string key, CancellationToken token = default) => Task.FromResult(Get(key));

            public void Refresh(string key)
            {
            }

            public Task RefreshAsync(string key, CancellationToken token = default) => Task.CompletedTask;

            public void Remove(string key) => _values.Remove(key);

            public Task RemoveAsync(string key, CancellationToken token = default)
            {
                Remove(key);
                return Task.CompletedTask;
            }

            public void Set(string key, byte[] value, DistributedCacheEntryOptions options) => _values[key] = value;

            public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default)
            {
                Set(key, value, options);
                return Task.CompletedTask;
            }
        }
    }
}
