using BiatecMCP.BusinessLogic;
using Moq;

namespace BiatecMCPTests
{
    /// <summary>
    /// Covers <see cref="DexSwapAggregatorService"/>: fanning out to every configured
    /// <see cref="IDexQuoteProvider"/> in parallel, tolerating individual provider failures (a broken or
    /// unreachable aggregator must never take down the whole quote comparison), and picking the best quote.
    /// </summary>
    [TestFixture]
    public class DexSwapAggregatorServiceTests
    {
        private static Mock<IDexQuoteProvider> MakeProvider(string name, long? outputAmount, bool throws = false)
        {
            var mock = new Mock<IDexQuoteProvider>();
            mock.Setup(p => p.ProviderName).Returns(name);
            if (throws)
            {
                mock.Setup(p => p.GetQuoteAsync(It.IsAny<long>(), It.IsAny<long>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
                    .ThrowsAsync(new HttpRequestException("unreachable"));
            }
            else
            {
                mock.Setup(p => p.GetQuoteAsync(It.IsAny<long>(), It.IsAny<long>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(outputAmount.HasValue ? new DexQuote { ProviderName = name, OutputAmount = outputAmount.Value } : null);
            }
            return mock;
        }

        [Test]
        public async Task GetAllQuotesAsync_AllProvidersSucceed_ReturnsAllQuotes()
        {
            var service = new DexSwapAggregatorService(new[]
            {
                MakeProvider("A", 100).Object,
                MakeProvider("B", 200).Object,
                MakeProvider("C", 150).Object
            });

            var quotes = await service.GetAllQuotesAsync(0, 31566704, 1_000_000);

            Assert.That(quotes, Has.Count.EqualTo(3));
        }

        [Test]
        public async Task GetAllQuotesAsync_OneProviderThrows_StillReturnsTheOtherTwo()
        {
            var service = new DexSwapAggregatorService(new[]
            {
                MakeProvider("A", 100).Object,
                MakeProvider("B", null, throws: true).Object,
                MakeProvider("C", 150).Object
            });

            var quotes = await service.GetAllQuotesAsync(0, 31566704, 1_000_000);

            Assert.That(quotes, Has.Count.EqualTo(2));
            Assert.That(quotes.Select(q => q.ProviderName), Is.EquivalentTo(new[] { "A", "C" }));
        }

        [Test]
        public async Task GetAllQuotesAsync_ProviderReturnsNull_IsExcludedNotFailed()
        {
            var service = new DexSwapAggregatorService(new[]
            {
                MakeProvider("A", 100).Object,
                MakeProvider("B", null).Object
            });

            var quotes = await service.GetAllQuotesAsync(0, 31566704, 1_000_000);

            Assert.That(quotes, Has.Count.EqualTo(1));
            Assert.That(quotes[0].ProviderName, Is.EqualTo("A"));
        }

        [Test]
        public async Task GetAllQuotesAsync_AllProvidersFail_ReturnsEmptyList()
        {
            var service = new DexSwapAggregatorService(new[]
            {
                MakeProvider("A", null, throws: true).Object,
                MakeProvider("B", null, throws: true).Object
            });

            var quotes = await service.GetAllQuotesAsync(0, 31566704, 1_000_000);

            Assert.That(quotes, Is.Empty);
        }

        [Test]
        public void PickBest_ReturnsHighestOutputAmount()
        {
            var quotes = new List<DexQuote>
            {
                new() { ProviderName = "A", OutputAmount = 100 },
                new() { ProviderName = "B", OutputAmount = 250 },
                new() { ProviderName = "C", OutputAmount = 150 }
            };

            var best = DexSwapAggregatorService.PickBest(quotes);

            Assert.That(best!.ProviderName, Is.EqualTo("B"));
        }

        [Test]
        public void PickBest_EmptyList_ReturnsNull()
        {
            Assert.That(DexSwapAggregatorService.PickBest(new List<DexQuote>()), Is.Null);
        }
    }
}
