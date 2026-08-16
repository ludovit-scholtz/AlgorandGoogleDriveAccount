using BiatecOIDC.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Moq;
using StackExchange.Redis;

namespace BiatecOIDCTests
{
    [TestFixture]
    public class RedisHealthCheckTests
    {
        [Test]
        public async Task CheckHealthAsync_RedisResponds_ReturnsHealthy()
        {
            var database = new Mock<IDatabase>();
            database.Setup(d => d.PingAsync(It.IsAny<CommandFlags>())).ReturnsAsync(TimeSpan.FromMilliseconds(3));
            var multiplexer = new Mock<IConnectionMultiplexer>();
            multiplexer.Setup(m => m.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(database.Object);

            var check = new RedisHealthCheck(multiplexer.Object);

            var result = await check.CheckHealthAsync(new HealthCheckContext());

            Assert.That(result.Status, Is.EqualTo(HealthStatus.Healthy));
        }

        [Test]
        public async Task CheckHealthAsync_RedisThrows_ReturnsUnhealthy()
        {
            var multiplexer = new Mock<IConnectionMultiplexer>();
            multiplexer.Setup(m => m.GetDatabase(It.IsAny<int>(), It.IsAny<object>()))
                .Throws(new RedisConnectionException(ConnectionFailureType.UnableToConnect, "no connection"));

            var check = new RedisHealthCheck(multiplexer.Object);

            var result = await check.CheckHealthAsync(new HealthCheckContext());

            Assert.That(result.Status, Is.EqualTo(HealthStatus.Unhealthy));
        }
    }
}
