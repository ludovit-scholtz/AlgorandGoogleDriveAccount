using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using BiatecMCP.BusinessLogic;
using BiatecMCP.Model;

namespace BiatecMCPTests
{
    [TestFixture]
    public class BiatecWalletClientTests
    {
        private sealed class FakeHttpMessageHandler : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
            public HttpRequestMessage? LastRequest { get; private set; }
            public string? LastRequestBody { get; private set; }

            public FakeHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
            {
                _respond = respond;
            }

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                LastRequest = request;
                LastRequestBody = request.Content == null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
                return _respond(request);
            }
        }

        private static IBiatecWalletClient CreateClient(FakeHttpMessageHandler handler, out FakeHttpMessageHandler capturedHandler)
        {
            capturedHandler = handler;
            var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://oidc.biatec.io/") };
            return new BiatecWalletClient(httpClient);
        }

        [Test]
        public async Task SignAsync_ForwardsBearerTokenAndBase64EncodedTransactions()
        {
            var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new SignTransactionGroupResponse { SignedTransactions = { "c2lnbmVk" } })
            });
            var client = CreateClient(handler, out var captured);

            var result = await client.SignAsync("the-bearer-token", new[] { new byte[] { 1, 2, 3 } });

            Assert.That(result.SignedTransactions, Is.EqualTo(new[] { "c2lnbmVk" }));
            Assert.That(captured.LastRequest!.RequestUri!.AbsolutePath, Is.EqualTo("/wallet/sign"));
            Assert.That(captured.LastRequest.Headers.Authorization!.Scheme, Is.EqualTo("Bearer"));
            Assert.That(captured.LastRequest.Headers.Authorization.Parameter, Is.EqualTo("the-bearer-token"));
            using var body = JsonDocument.Parse(captured.LastRequestBody!);
            var sentTransactions = body.RootElement.GetProperty("transactions").EnumerateArray().Select(e => e.GetString()).ToList();
            Assert.That(sentTransactions, Is.EqualTo(new[] { Convert.ToBase64String(new byte[] { 1, 2, 3 }) }));
        }

        [Test]
        public void SignAsync_SpendingLimitExceeded_ThrowsWalletApiExceptionWithProblemDetails()
        {
            var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = new StringContent(
                    """{"title":"spending_limit_exceeded","detail":"Daily limit of $100 would be exceeded."}""",
                    Encoding.UTF8, "application/problem+json")
            });
            var client = CreateClient(handler, out _);

            var ex = Assert.ThrowsAsync<WalletApiException>(async () => await client.SignAsync("token", new[] { new byte[] { 1 } }));

            Assert.That(ex!.StatusCode, Is.EqualTo(403));
            Assert.That(ex.ErrorCode, Is.EqualTo("spending_limit_exceeded"));
            Assert.That(ex.Message, Does.Contain("Daily limit"));
        }

        [Test]
        public void SignAsync_InsufficientScope_ThrowsWalletApiExceptionWithCorrectErrorCode()
        {
            var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = new StringContent(
                    """{"title":"insufficient_scope","detail":"This transaction group contains a rekey transaction."}""",
                    Encoding.UTF8, "application/problem+json")
            });
            var client = CreateClient(handler, out _);

            var ex = Assert.ThrowsAsync<WalletApiException>(async () => await client.SignAsync("token", new[] { new byte[] { 1 } }));

            Assert.That(ex!.ErrorCode, Is.EqualTo("insufficient_scope"));
        }

        [Test]
        public void SignAsync_NonProblemDetailsErrorBody_StillThrowsWithStatusCodeDerivedErrorCode()
        {
            var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.BadGateway)
            {
                Content = new StringContent("<html>upstream proxy error</html>", Encoding.UTF8, "text/html")
            });
            var client = CreateClient(handler, out _);

            var ex = Assert.ThrowsAsync<WalletApiException>(async () => await client.SignAsync("token", new[] { new byte[] { 1 } }));

            Assert.That(ex!.StatusCode, Is.EqualTo(502));
        }

        [Test]
        public async Task SignAsync_WithPrimaryAddressAndSlot_IncludesThemInRequestBody()
        {
            var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new SignTransactionGroupResponse { SignedTransactions = { "c2lnbmVk" } })
            });
            var client = CreateClient(handler, out var captured);

            await client.SignAsync("token", new[] { new byte[] { 1 } }, primaryAddress: "SEED-ADDR", slot: 5);

            using var body = JsonDocument.Parse(captured.LastRequestBody!);
            Assert.That(body.RootElement.GetProperty("primaryAddress").GetString(), Is.EqualTo("SEED-ADDR"));
            Assert.That(body.RootElement.GetProperty("slot").GetInt32(), Is.EqualTo(5));
        }

        [Test]
        public async Task ListAddressesAsync_ForwardsBearerTokenAndParsesResponse()
        {
            var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new ListAddressesResponse
                {
                    Addresses =
                    {
                        new AddressResponse { Address = "ADDR1", IsPrimary = false },
                        new AddressResponse { Address = "ADDR2", IsPrimary = true }
                    }
                })
            });
            var client = CreateClient(handler, out var captured);

            var result = await client.ListAddressesAsync("the-bearer-token");

            Assert.That(captured.LastRequest!.Method, Is.EqualTo(HttpMethod.Get));
            Assert.That(captured.LastRequest.RequestUri!.AbsolutePath, Is.EqualTo("/wallet/address"));
            Assert.That(captured.LastRequest.Headers.Authorization!.Parameter, Is.EqualTo("the-bearer-token"));
            Assert.That(result.Addresses, Has.Count.EqualTo(2));
            Assert.That(result.Addresses.Single(a => a.IsPrimary).Address, Is.EqualTo("ADDR2"));
        }

        [Test]
        public async Task GetAddressAsync_ForwardsBearerTokenAndParsesResponse()
        {
            var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new DerivedAddressResponse { Address = "DERIVED", PrimaryAddress = "ADDR1", Slot = 3 })
            });
            var client = CreateClient(handler, out var captured);

            var result = await client.GetAddressAsync("the-bearer-token", "ADDR1", 3);

            Assert.That(captured.LastRequest!.Method, Is.EqualTo(HttpMethod.Get));
            Assert.That(captured.LastRequest.RequestUri!.AbsolutePath, Is.EqualTo("/wallet/address/ADDR1/3"));
            Assert.That(result.Address, Is.EqualTo("DERIVED"));
        }

        [Test]
        public void GetAddressAsync_UnknownSeed_ThrowsWalletApiException()
        {
            var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent(
                    """{"title":"seed_not_found","detail":"No seed with address 'X' exists."}""",
                    Encoding.UTF8, "application/problem+json")
            });
            var client = CreateClient(handler, out _);

            var ex = Assert.ThrowsAsync<WalletApiException>(async () => await client.GetAddressAsync("token", "X", 0));

            Assert.That(ex!.ErrorCode, Is.EqualTo("seed_not_found"));
        }

        [Test]
        public async Task ListSeedsAsync_ForwardsBearerTokenAndParsesResponse()
        {
            var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new ListSeedsResponse
                {
                    Seeds =
                    {
                        new SeedResponse { Address = "ADDR1", CreatedUtc = DateTimeOffset.UnixEpoch, IsPrimary = false },
                        new SeedResponse { Address = "ADDR2", CreatedUtc = DateTimeOffset.UnixEpoch, IsPrimary = true }
                    }
                })
            });
            var client = CreateClient(handler, out var captured);

            var result = await client.ListSeedsAsync("the-bearer-token");

            Assert.That(captured.LastRequest!.Method, Is.EqualTo(HttpMethod.Get));
            Assert.That(captured.LastRequest.RequestUri!.AbsolutePath, Is.EqualTo("/wallet/seeds"));
            Assert.That(result.Seeds, Has.Count.EqualTo(2));
            Assert.That(result.Seeds.Single(s => s.IsPrimary).Address, Is.EqualTo("ADDR2"));
        }
    }
}
