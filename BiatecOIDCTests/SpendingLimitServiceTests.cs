using BiatecOIDC.BusinessLogic;
using Microsoft.Extensions.Logging;
using Moq;
using StackExchange.Redis;

namespace BiatecOIDCTests
{
    [TestFixture]
    public class SpendingLimitServiceTests
    {
        private const string TestEmail = "user@example.com";

        private Mock<IConnectionMultiplexer> _mockRedis = null!;
        private Mock<IDatabase> _mockDatabase = null!;
        private Mock<ILogger<SpendingLimitService>> _mockLogger = null!;
        private SpendingLimitService _service = null!;

        [SetUp]
        public void SetUp()
        {
            _mockRedis = new Mock<IConnectionMultiplexer>();
            _mockDatabase = new Mock<IDatabase>();
            _mockRedis.Setup(r => r.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(_mockDatabase.Object);
            _mockLogger = new Mock<ILogger<SpendingLimitService>>();
            _service = new SpendingLimitService(_mockRedis.Object, _mockLogger.Object);
        }

        [Test]
        public async Task GetMaxAmountPerTransactionAsync_NoRecordStored_ReturnsZeroUnbounded()
        {
            _mockDatabase
                .Setup(d => d.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
                .ReturnsAsync(RedisValue.Null);

            var result = await _service.GetMaxAmountPerTransactionAsync(TestEmail);

            Assert.That(result, Is.EqualTo(0UL));
        }

        [Test]
        public async Task SetThenGet_RoundTripsTheConfiguredLimit()
        {
            string? stored = null;
            _mockDatabase
                .Setup(d => d.StringSetAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), null, false, When.Always, CommandFlags.None))
                .Callback<RedisKey, RedisValue, TimeSpan?, bool, When, CommandFlags>((_, value, _, _, _, _) => stored = value!)
                .ReturnsAsync(true);
            _mockDatabase
                .Setup(d => d.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
                .ReturnsAsync(() => stored ?? RedisValue.Null);

            await _service.SetMaxAmountPerTransactionAsync(TestEmail, 5_000_000UL);
            var result = await _service.GetMaxAmountPerTransactionAsync(TestEmail);

            Assert.That(result, Is.EqualTo(5_000_000UL));
        }

        [Test]
        public async Task SetMaxAmountPerTransactionAsync_UsesEmailScopedKey_CaseAndWhitespaceNormalized()
        {
            RedisKey? capturedKey = null;
            _mockDatabase
                .Setup(d => d.StringSetAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), null, false, When.Always, CommandFlags.None))
                .Callback<RedisKey, RedisValue, TimeSpan?, bool, When, CommandFlags>((key, _, _, _, _, _) => capturedKey = key)
                .ReturnsAsync(true);

            await _service.SetMaxAmountPerTransactionAsync("  User@Example.com  ", 100);
            var normalizedKey = capturedKey.ToString();

            await _service.SetMaxAmountPerTransactionAsync(TestEmail, 100);
            var lowercaseKey = capturedKey.ToString();

            Assert.That(normalizedKey, Is.EqualTo(lowercaseKey));
        }

        [Test]
        public void GetMaxAmountPerTransactionAsync_EmptyEmail_Throws()
        {
            Assert.ThrowsAsync<ArgumentException>(async () => await _service.GetMaxAmountPerTransactionAsync(string.Empty));
        }

        [Test]
        public void SetMaxAmountPerTransactionAsync_EmptyEmail_Throws()
        {
            Assert.ThrowsAsync<ArgumentException>(async () => await _service.SetMaxAmountPerTransactionAsync(string.Empty, 1));
        }

        [Test]
        public async Task GetMaxAmountPerTransactionAsync_CorruptRecord_ReturnsZeroUnbounded()
        {
            _mockDatabase
                .Setup(d => d.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
                .ReturnsAsync((RedisValue)"{not valid json");

            var result = await _service.GetMaxAmountPerTransactionAsync(TestEmail);

            Assert.That(result, Is.EqualTo(0UL));
        }
    }
}
