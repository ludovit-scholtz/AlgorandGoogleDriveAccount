using System.Net;
using System.Text;
using BiatecOIDC.BusinessLogic;
using NUnit.Framework;

namespace BiatecOIDCTests
{
    /// <summary>
    /// <see cref="BiatecRouterValuationServiceTests"/> mocks <see cref="IBiatecRouterQuoteClient"/>, so nothing there runs the
    /// real BiatecRouterConnector. This drives the real connector end to end against a stubbed HTTP handler, to catch a
    /// connector upgrade that changes the /quote contract (path/response type) or no longer binds to the loaded Algorand SDK.
    /// </summary>
    [TestFixture]
    public class BiatecRouterQuoteClientTests
    {
        private sealed class StubHandler : HttpMessageHandler
        {
            public HttpRequestMessage? LastRequest { get; private set; }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                LastRequest = request;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("123456", Encoding.UTF8, "application/json")
                });
            }
        }

        [Test]
        public async Task QuoteAsync_RealConnectorAgainstStubbedRouter_ReturnsQuotedAmountFromQuoteEndpoint()
        {
            var handler = new StubHandler();
            using var http = new HttpClient(handler);
            var client = new BiatecRouterQuoteClient(http);

            var quote = await client.QuoteAsync(0UL, 31566704UL, 1_000_000UL);

            Assert.That(quote, Is.EqualTo(123456UL));
            Assert.That(handler.LastRequest!.Method, Is.EqualTo(HttpMethod.Get));
            Assert.That(handler.LastRequest.RequestUri!.AbsolutePath, Does.Contain("quote").IgnoreCase);
            Assert.That(handler.LastRequest.RequestUri!.Query, Is.EqualTo("?fromAsset=0&toAsset=31566704&amount=1000000"));
        }
    }
}
