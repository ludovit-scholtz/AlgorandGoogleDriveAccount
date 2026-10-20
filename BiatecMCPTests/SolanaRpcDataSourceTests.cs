using System.Net;
using System.Text;
using BiatecMCP.BusinessLogic;
using BiatecMCP.Helper;
using BiatecMCP.Model;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace BiatecMCPTests
{
    [TestFixture]
    public class SolanaRpcDataSourceTests
    {
        private sealed class StubHandler : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

            public string? LastBody { get; private set; }

            public Uri? LastUri { get; private set; }

            public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                LastUri = request.RequestUri;
                LastBody = request.Content == null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
                return _respond(request);
            }
        }

        private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
            new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

        private static SolanaRpcDataSource Create(StubHandler handler, string rpcUrl = "https://rpc.example.com/") =>
            new(new HttpClient(handler),
                Mock.Of<IOptionsMonitor<SolanaConfiguration>>(m => m.CurrentValue == new SolanaConfiguration { RpcUrl = rpcUrl }),
                NullLogger<SolanaRpcDataSource>.Instance);

        [Test]
        public async Task TryGetBalanceAsync_ParsesLamportsAndSendsAddressToConfiguredNode()
        {
            var handler = new StubHandler(_ => Json("{\"jsonrpc\":\"2.0\",\"result\":{\"context\":{\"slot\":1},\"value\":123456789},\"id\":1}"));

            var balance = await Create(handler).TryGetBalanceAsync("SomeAddress");

            Assert.That(balance, Is.EqualTo(123456789UL));
            Assert.That(handler.LastUri!.ToString(), Is.EqualTo("https://rpc.example.com/"));
            Assert.That(handler.LastBody, Does.Contain("\"method\":\"getBalance\"").And.Contain("SomeAddress"));
        }

        [Test]
        public async Task TryGetBalanceAsync_RpcError_ReturnsNull()
        {
            var handler = new StubHandler(_ => Json("{\"jsonrpc\":\"2.0\",\"error\":{\"code\":-32602,\"message\":\"Invalid param\"},\"id\":1}"));

            Assert.That(await Create(handler).TryGetBalanceAsync("bad"), Is.Null);
        }

        [Test]
        public async Task TryGetBalanceAsync_HttpFailure_ReturnsNull()
        {
            var handler = new StubHandler(_ => Json("rate limited", HttpStatusCode.TooManyRequests));

            Assert.That(await Create(handler).TryGetBalanceAsync("addr"), Is.Null);
        }

        [Test]
        public async Task TryGetBalanceAsync_ConnectionFailure_ReturnsNullInsteadOfThrowing()
        {
            var handler = new StubHandler(_ => throw new HttpRequestException("boom"));

            Assert.That(await Create(handler).TryGetBalanceAsync("addr"), Is.Null);
        }

        [Test]
        public async Task TryGetLatestBlockhashAsync_ParsesBlockhash()
        {
            var handler = new StubHandler(_ => Json("{\"jsonrpc\":\"2.0\",\"result\":{\"context\":{\"slot\":2},\"value\":{\"blockhash\":\"EkSnNWid2cvwEVnVx9aBqawnmiCNiDgp3gUdkDPTKN1N\",\"lastValidBlockHeight\":3}},\"id\":1}"));

            var blockhash = await Create(handler).TryGetLatestBlockhashAsync();

            Assert.That(blockhash, Is.EqualTo("EkSnNWid2cvwEVnVx9aBqawnmiCNiDgp3gUdkDPTKN1N"));
            Assert.That(handler.LastBody, Does.Contain("getLatestBlockhash").And.Contain("finalized"));
        }

        [Test]
        public async Task TryGetLatestBlockhashAsync_MalformedResult_ReturnsNull()
        {
            var handler = new StubHandler(_ => Json("{\"jsonrpc\":\"2.0\",\"result\":{\"value\":{}},\"id\":1}"));

            Assert.That(await Create(handler).TryGetLatestBlockhashAsync(), Is.Null);
        }

        [Test]
        public async Task SendTransactionAsync_ReturnsSignatureAndSendsBase64Payload()
        {
            var handler = new StubHandler(_ => Json("{\"jsonrpc\":\"2.0\",\"result\":\"5VERv8NMvzbJMEkV8xnrLkEaWRtSz9CosKDYjCJjBRnbJLgp8uirBgmQpjKhoR4tjF3ZpRzrFmBV6UjKdiSZkQUW\",\"id\":1}"));

            var result = await Create(handler).SendTransactionAsync(new byte[] { 1, 2, 3 });

            Assert.That(result.Error, Is.Null);
            Assert.That(result.Signature, Does.StartWith("5VERv8"));
            Assert.That(handler.LastBody, Does.Contain("sendTransaction").And.Contain(Convert.ToBase64String(new byte[] { 1, 2, 3 })).And.Contain("base64"));
        }

        [Test]
        public async Task SendTransactionAsync_NodeError_ReturnsNodeMessage()
        {
            var handler = new StubHandler(_ => Json("{\"jsonrpc\":\"2.0\",\"error\":{\"code\":-32002,\"message\":\"Transaction simulation failed: insufficient funds\"},\"id\":1}"));

            var result = await Create(handler).SendTransactionAsync(new byte[] { 1 });

            Assert.That(result.Signature, Is.Null);
            Assert.That(result.Error, Does.Contain("insufficient funds"));
        }

        [Test]
        public async Task SendTransactionAsync_PreflightFailure_IncludesTheNodesErrDetail()
        {
            // Real shape returned by mainnet-beta for a signed transfer from an unfunded account.
            var handler = new StubHandler(_ => Json("{\"jsonrpc\":\"2.0\",\"error\":{\"code\":-32002,\"message\":\"Transaction simulation failed\",\"data\":{\"err\":\"AccountNotFound\",\"logs\":[]}},\"id\":1}"));

            var result = await Create(handler).SendTransactionAsync(new byte[] { 1 });

            Assert.That(result.Error, Is.EqualTo("Transaction simulation failed (AccountNotFound)"));
        }

        [Test]
        public async Task SendTransactionAsync_StructuredErr_IncludesRawJson()
        {
            var handler = new StubHandler(_ => Json("{\"jsonrpc\":\"2.0\",\"error\":{\"code\":-32002,\"message\":\"Transaction simulation failed\",\"data\":{\"err\":{\"InstructionError\":[0,{\"Custom\":1}]}}},\"id\":1}"));

            var result = await Create(handler).SendTransactionAsync(new byte[] { 1 });

            Assert.That(result.Error, Does.Contain("InstructionError"));
        }

        [Test]
        public async Task SendTransactionAsync_Unreachable_ReturnsGenericError()
        {
            var handler = new StubHandler(_ => throw new HttpRequestException("boom"));

            var result = await Create(handler).SendTransactionAsync(new byte[] { 1 });

            Assert.That(result.Signature, Is.Null);
            Assert.That(result.Error, Does.Contain("unreachable"));
        }

        [Test]
        public async Task SendTransactionAsync_NoResultAndNoError_ReturnsError()
        {
            var handler = new StubHandler(_ => Json("{\"jsonrpc\":\"2.0\",\"id\":1}"));

            var result = await Create(handler).SendTransactionAsync(new byte[] { 1 });

            Assert.That(result.Signature, Is.Null);
            Assert.That(result.Error, Is.Not.Null);
        }
    }

    [TestFixture]
    public class SolanaAddressValidatorTests
    {
        [TestCase("4nrWTvCH3vGghq2FXd22mifh3rKrKAXHw8xWJxt6tZ4H")]
        [TestCase("FoBNuzYQ4gpiP3jmevoiRVp3EnqBeXkB2HJ1LksSwYXJ")]
        [TestCase("11111111111111111111111111111111")] // System Program: 32 leading '1' -> 32 zero bytes
        [TestCase("EkSnNWid2cvwEVnVx9aBqawnmiCNiDgp3gUdkDPTKN1N")] // a blockhash is the same 32-byte base58 shape
        public void IsValid_RealThirtyTwoByteKeys_ReturnsTrue(string value) =>
            Assert.That(SolanaAddressValidator.IsValid(value), Is.True);

        [TestCase(null)]
        [TestCase("")]
        [TestCase("   ")]
        [TestCase("0x13f022d72158410433cbd66f5dd8bf6d2d0999c1")] // EVM address
        [TestCase("bc1qar0srrr7xfkvy5l643lydnw9re59gtzzwf5mdq")] // Bitcoin bech32
        [TestCase("4nrWTvCH3vGghq2FXd22mifh3rKrKAXHw8xWJxt6tZ4")] // truncated: decodes to 31 bytes
        [TestCase("4nrWTvCH3vGghq2FXd22mifh3rKrKAXHw8xWJxt6tZ4HH")] // too long: decodes to >32 bytes
        [TestCase("4nrWTvCH3vGghq2FXd22mifh3rKrKAXHw8xWJxt6tZ4O")] // 'O' is not in the base58 alphabet
        [TestCase("4nrWTvCH3vGghq2FXd22mifh3rKrKAXHw8xWJxt6tZ4l")] // 'l' is not in the base58 alphabet
        public void IsValid_NotAThirtyTwoByteBase58Key_ReturnsFalse(string? value) =>
            Assert.That(SolanaAddressValidator.IsValid(value), Is.False);
    }
}
