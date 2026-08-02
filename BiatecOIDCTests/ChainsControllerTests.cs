using BiatecOIDC.BusinessLogic;
using BiatecOIDC.Controllers;
using BiatecOIDC.Model;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;

namespace BiatecOIDCTests
{
    [TestFixture]
    public class ChainsControllerTests
    {
        private Mock<IAlgorandChainRegistry> _chainRegistry = null!;
        private ChainsController _controller = null!;

        [SetUp]
        public void SetUp()
        {
            _chainRegistry = new Mock<IAlgorandChainRegistry>();
            _controller = new ChainsController(_chainRegistry.Object, new Mock<ILogger<ChainsController>>().Object);
        }

        [Test]
        public async Task GetChains_ReturnsRegistrysSupportedChains_WithoutExposingAuthToken()
        {
            _chainRegistry.Setup(r => r.GetSupportedChainsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new[]
            {
                new AlgorandChain
                {
                    GenesisId = "mainnet-v1.0",
                    Name = "Algorand Mainnet",
                    GenesisHash = "SOMEHASH==",
                    AlgodApiAddress = "https://mainnet-api.example.com",
                    AlgodApiToken = "super-secret-token",
                    AlgodApiTokenHeader = "X-Algo-API-Token"
                }
            });

            var result = await _controller.GetChains();

            var ok = result as OkObjectResult;
            Assert.That(ok, Is.Not.Null);
            var response = ok!.Value as ChainsResponse;
            Assert.That(response, Is.Not.Null);
            Assert.That(response!.Chains, Has.Count.EqualTo(1));
            Assert.That(response.Chains[0].GenesisId, Is.EqualTo("mainnet-v1.0"));
            Assert.That(response.Chains[0].AlgodApiAddress, Is.EqualTo("https://mainnet-api.example.com"));
        }

        [Test]
        public async Task GetChains_NoSupportedChains_ReturnsEmptyList()
        {
            _chainRegistry.Setup(r => r.GetSupportedChainsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<AlgorandChain>());

            var result = await _controller.GetChains();

            var ok = result as OkObjectResult;
            var response = ok!.Value as ChainsResponse;
            Assert.That(response!.Chains, Is.Empty);
        }

        [Test]
        public async Task GetChains_RegistryThrows_ReturnsServiceUnavailable()
        {
            _chainRegistry.Setup(r => r.GetSupportedChainsAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("unreachable"));

            var result = await _controller.GetChains();

            var statusResult = result as ObjectResult;
            Assert.That(statusResult, Is.Not.Null);
            Assert.That(statusResult!.StatusCode, Is.EqualTo(503));
        }
    }
}
