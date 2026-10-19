using NUnit.Framework;

namespace BiatecMCPTests
{
    /// <summary>
    /// BiatecRouterConnector uses types from the Algorand SDK. Its swap path is otherwise
    /// only exercised against mocks, so this calls the one connector API that binds to an Algorand type for real - a
    /// MissingMethodException/TypeLoadException here means the connector and the SDK are no longer binary compatible.
    /// </summary>
    [TestFixture]
    public class BiatecRouterConnectorCompatibilityTests
    {
        [Test]
        public void ToRouterParams_WithAlgorand5TransactionParameters_MapsWithoutBindingErrors()
        {
            var suggested = new Algorand.Algod.Model.TransactionParametersResponse
            {
                GenesisId = "mainnet-v1.0",
                LastRound = 12345,
                MinFee = 1000,
                Fee = 0
            };

            var routerParams = BiatecRouterConnector.Extensions.ToRouterParams(suggested);

            Assert.That(routerParams, Is.Not.Null);
        }
    }
}
