using System.Net;
using System.Text;
using BiatecMCP.BusinessLogic;
using NUnit.Framework;

namespace BiatecMCPTests
{
    /// <summary>
    /// Drives the real BiatecRouterConnector (not a mock) through <see cref="BiatecRouterQuoteProvider"/> against a stubbed
    /// HTTP handler, covering both calls createSwapTransaction relies on (/quote and the route-building call): request
    /// shaping and response deserialization. The stubbed response bodies are hand-written, and router-side auth is not
    /// modelled, so this guards against connector binding/contract drift, not against the live router's behaviour.
    /// </summary>
    [TestFixture]
    public class BiatecRouterQuoteProviderTests
    {
        private sealed class StubHandler : HttpMessageHandler
        {
            private readonly string _body;
            public HttpRequestMessage? LastRequest { get; private set; }
            public string? LastRequestBody { get; private set; }

            public StubHandler(string body) => _body = body;

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                LastRequest = request;
                LastRequestBody = request.Content == null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(_body, Encoding.UTF8, "application/json") };
            }
        }

        [Test]
        public async Task GetQuoteAsync_RealConnector_ReturnsQuotedAmountAndSendsAssetsAndAmount()
        {
            var handler = new StubHandler("123456");
            using var http = new HttpClient(handler);

            var quote = await new BiatecRouterQuoteProvider(http).GetQuoteAsync(0, 31566704, 1_000_000);

            Assert.That(quote, Is.Not.Null);
            Assert.That(quote!.OutputAmount, Is.EqualTo(123456));
            Assert.That(handler.LastRequest!.RequestUri!.AbsolutePath, Does.Contain("quote").IgnoreCase);
            var query = System.Web.HttpUtility.ParseQueryString(handler.LastRequest.RequestUri.Query);
            Assert.That(query["fromAsset"], Is.EqualTo("0"));
            Assert.That(query["toAsset"], Is.EqualTo("31566704"));
            Assert.That(query["amount"], Is.EqualTo("1000000"));
        }

        [Test]
        public async Task BuildRouteTransactionsAsync_RealConnector_ReturnsTransactionsToSignAndSendsRouteInput()
        {
            var handler = new StubHandler("{\"routes\":[{\"txsToSign\":[\"AAEC\",\"AwQF\"]}]}");
            using var http = new HttpClient(handler);
            var routerParams = new BiatecRouterConnector.Generated.TransactionParametersResponse { GenesisId = "mainnet-v1.0" };

            var txs = await new BiatecRouterQuoteProvider(http).BuildRouteTransactionsAsync(
                "SENDERADDRESS", 0, 31566704, 1_000_000, 900_000, routerParams);

            Assert.That(txs, Is.EqualTo(new[] { "AAEC", "AwQF" }));
            Assert.That(handler.LastRequestBody, Does.Contain("SENDERADDRESS").And.Contain("31566704").And.Contain("1000000").And.Contain("900000"));
        }
    }
}
