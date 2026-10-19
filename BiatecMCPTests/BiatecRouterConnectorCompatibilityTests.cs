using NUnit.Framework;

namespace BiatecMCPTests
{
    /// <summary>
    /// ToRouterParams is the one BiatecRouterConnector API that takes an Algorand SDK type (everything else uses the connector's own
    /// generated models), so it is the one place the connector binds to Algorand.dll directly. A
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
