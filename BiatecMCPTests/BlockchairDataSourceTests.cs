using System.Net;
using System.Net.Http.Json;
using System.Text;
using BiatecMCP.BusinessLogic;
using Microsoft.Extensions.Logging.Abstractions;

namespace BiatecMCPTests
{
    /// <summary>
    /// Covers <see cref="BlockchairDataSource"/>'s address handling - in particular, the "bitcoincash:"
    /// URI-scheme prefix that every BCH address this codebase derives includes
    /// (<c>CloudAccountRepository.DeriveBitcoinCashAddressAsync</c>) but that Blockchair's API rejects both
    /// in the URL path and as the response's own dictionary key. This is the one piece of this class
    /// exercised without live network access (request-shape/parsing logic) - the actual HTTP behavior still
    /// needs manual/E2E verification, per <see cref="IPublicBitcoinDataSource"/>'s remarks.
    /// </summary>
    [TestFixture]
    public class BlockchairDataSourceTests
    {
        private sealed class FakeHttpMessageHandler : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
            public HttpRequestMessage? LastRequest { get; private set; }

            public FakeHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
            {
                _respond = respond;
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                LastRequest = request;
                return Task.FromResult(_respond(request));
            }
        }

        private static BlockchairDataSource CreateSource(FakeHttpMessageHandler handler)
        {
            var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.blockchair.com/") };
            return new BlockchairDataSource(httpClient, NullLogger<BlockchairDataSource>.Instance);
        }

        [Test]
        public async Task TryGetBalanceAsync_BitcoinCashAddress_StripsPrefixFromRequestUrl()
        {
            const string prefixedAddress = "bitcoincash:qqhzqtrtm0529txst2t4hcejnx35w6g0lu0ux9rjyu";
            const string bareAddress = "qqhzqtrtm0529txst2t4hcejnx35w6g0lu0ux9rjyu";
            var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new { data = new Dictionary<string, object> { [bareAddress] = new { address = new { balance = 123456 } } } })
            });
            var source = CreateSource(handler);

            var balance = await source.TryGetBalanceAsync(BlockchairChainSlugs.BitcoinCash, prefixedAddress);

            Assert.That(balance, Is.EqualTo(123456));
            Assert.That(handler.LastRequest!.RequestUri!.AbsolutePath, Does.Contain(bareAddress));
            Assert.That(handler.LastRequest.RequestUri!.AbsolutePath, Does.Not.Contain("bitcoincash"));
        }

        [Test]
        public async Task TryGetBalanceAsync_BitcoinCashAddressPrefixIsCaseInsensitive()
        {
            const string prefixedAddress = "BitcoinCash:qqhzqtrtm0529txst2t4hcejnx35w6g0lu0ux9rjyu";
            const string bareAddress = "qqhzqtrtm0529txst2t4hcejnx35w6g0lu0ux9rjyu";
            var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new { data = new Dictionary<string, object> { [bareAddress] = new { address = new { balance = 1 } } } })
            });
            var source = CreateSource(handler);

            var balance = await source.TryGetBalanceAsync(BlockchairChainSlugs.BitcoinCash, prefixedAddress);

            Assert.That(balance, Is.EqualTo(1));
        }

        [Test]
        public async Task TryGetBalanceAsync_BitcoinAddress_UnaffectedByNormalization()
        {
            const string address = "1A1zP1eP5QGefi2DMPTfTL5SLmv7DivfNa";
            var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new { data = new Dictionary<string, object> { [address] = new { address = new { balance = 42 } } } })
            });
            var source = CreateSource(handler);

            var balance = await source.TryGetBalanceAsync(BlockchairChainSlugs.Bitcoin, address);

            Assert.That(balance, Is.EqualTo(42));
            Assert.That(handler.LastRequest!.RequestUri!.AbsolutePath, Does.Contain(address));
        }

        [Test]
        public async Task TryGetBalanceAsync_NotFoundResponse_ReturnsNullRatherThanThrowing()
        {
            var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
            var source = CreateSource(handler);

            var balance = await source.TryGetBalanceAsync(BlockchairChainSlugs.BitcoinCash, "bitcoincash:qqhzqtrtm0529txst2t4hcejnx35w6g0lu0ux9rjyu");

            Assert.That(balance, Is.Null);
        }

        [Test]
        public async Task GetUtxosAsync_BitcoinCashAddress_StripsPrefixFromRequestUrlAndResolvesUtxos()
        {
            const string prefixedAddress = "bitcoincash:qqhzqtrtm0529txst2t4hcejnx35w6g0lu0ux9rjyu";
            const string bareAddress = "qqhzqtrtm0529txst2t4hcejnx35w6g0lu0ux9rjyu";
            var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new
                {
                    data = new Dictionary<string, object>
                    {
                        [bareAddress] = new
                        {
                            utxo = new[]
                            {
                                new { transaction_hash = "TX1", index = 0, value = 100000000L }
                            }
                        }
                    }
                })
            });
            var source = CreateSource(handler);

            var utxos = await source.GetUtxosAsync(BlockchairChainSlugs.BitcoinCash, prefixedAddress);

            Assert.That(utxos, Has.Count.EqualTo(1));
            Assert.That(utxos[0].TxId, Is.EqualTo("TX1"));
            Assert.That(utxos[0].AmountSatoshis, Is.EqualTo(100000000L));
            Assert.That(handler.LastRequest!.RequestUri!.AbsolutePath, Does.Not.Contain("bitcoincash"));
        }

        [Test]
        public async Task GetUtxosAsync_AddressNotInResponse_ReturnsEmpty()
        {
            var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new { data = new Dictionary<string, object>() })
            });
            var source = CreateSource(handler);

            var utxos = await source.GetUtxosAsync(BlockchairChainSlugs.BitcoinCash, "bitcoincash:qqhzqtrtm0529txst2t4hcejnx35w6g0lu0ux9rjyu");

            Assert.That(utxos, Is.Empty);
        }

        [Test]
        public async Task TryGetSuggestedFeeRateAsync_ParsesSuggestedFee()
        {
            var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new { data = new { suggested_transaction_fee_per_byte_sat = 1.5m } })
            });
            var source = CreateSource(handler);

            var rate = await source.TryGetSuggestedFeeRateAsync(BlockchairChainSlugs.BitcoinCash);

            Assert.That(rate, Is.EqualTo(1.5m));
        }

        [Test]
        public async Task TryBroadcastAsync_SuccessResponse_ReturnsTransactionHash()
        {
            var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new { data = new { transaction_hash = "ABC123" } })
            });
            var source = CreateSource(handler);

            var txId = await source.TryBroadcastAsync(BlockchairChainSlugs.BitcoinCash, "deadbeef");

            Assert.That(txId, Is.EqualTo("ABC123"));
        }

        [Test]
        public async Task TryBroadcastAsync_ErrorStatusCode_ReturnsNullRatherThanThrowing()
        {
            var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("rejected", Encoding.UTF8, "text/plain")
            });
            var source = CreateSource(handler);

            var txId = await source.TryBroadcastAsync(BlockchairChainSlugs.BitcoinCash, "deadbeef");

            Assert.That(txId, Is.Null);
        }
    }
}
