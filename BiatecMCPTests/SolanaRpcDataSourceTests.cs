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
            Assert.That(handler.LastBody, Does.Contain("getLatestBlockhash").And.Contain("confirmed"));
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

        [TestCase("{\"jsonrpc\":\"2.0\",\"error\":{\"code\":-32002,\"message\":\"Transaction simulation failed: This transaction has already been processed\",\"data\":{\"err\":\"AlreadyProcessed\"}},\"id\":1}")]
        [TestCase("{\"jsonrpc\":\"2.0\",\"error\":{\"code\":-32002,\"message\":\"Transaction simulation failed\",\"data\":{\"err\":\"AlreadyProcessed\",\"logs\":[]}},\"id\":1}")]
        public async Task SendTransactionAsync_AlreadyProcessed_IsSuccessWithTheLocallyComputedSignature(string body)
        {
            // The advertised safe retry after an outcome-unknown broadcast: if the first attempt landed, resubmitting the same
            // signed bytes must read as success (never as a failed transfer an agent might rebuild and send twice).
            var handler = new StubHandler(_ => Json(body));
            var signed = new byte[1 + 64 + 10];
            signed[0] = 1;
            for (var i = 1; i <= 64; i++)
            {
                signed[i] = (byte)(i * 3);
            }

            var result = await Create(handler).SendTransactionAsync(signed);

            Assert.That(result.Error, Is.Null);
            Assert.That(result.Signature, Is.EqualTo(SolanaAddressValidator.TryComputeSignature(signed)));
        }

        [Test]
        public async Task SendTransactionAsync_Unreachable_ReturnsGenericError()
        {
            var handler = new StubHandler(_ => throw new HttpRequestException("boom"));

            var result = await Create(handler).SendTransactionAsync(new byte[] { 1 });

            Assert.That(result.Signature, Is.Null);
            Assert.That(result.OutcomeUnknown, Is.True);
            Assert.That(result.Error, Does.Contain("UNKNOWN"));
        }

        [Test]
        public async Task SendTransactionAsync_Unreachable_ReportsTheLocallyComputedSignatureToLookUp()
        {
            var handler = new StubHandler(_ => throw new TaskCanceledException("timeout"));
            var signed = new byte[1 + 64 + 10];
            signed[0] = 1;
            for (var i = 1; i <= 64; i++)
            {
                signed[i] = (byte)(i + 1);
            }

            var result = await Create(handler).SendTransactionAsync(signed);

            Assert.That(result.OutcomeUnknown, Is.True);
            Assert.That(result.ExpectedSignature, Is.EqualTo(SolanaAddressValidator.TryComputeSignature(signed)));
            Assert.That(result.ExpectedSignature, Is.Not.Null.And.Not.Empty);
        }

        [Test]
        public async Task SendTransactionAsync_ServerErrorStatus_IsOutcomeUnknownNotAConfirmedRejection()
        {
            var handler = new StubHandler(_ => Json("busy", HttpStatusCode.BadGateway));

            var result = await Create(handler).SendTransactionAsync(new byte[] { 1 });

            Assert.That(result.OutcomeUnknown, Is.True);
        }

        [TestCase(HttpStatusCode.TooManyRequests)]
        [TestCase(HttpStatusCode.Forbidden)]
        public async Task SendTransactionAsync_ClientErrorStatus_IsADefiniteNotAcceptedSoRetryingTheSameBytesIsSafe(HttpStatusCode status)
        {
            var handler = new StubHandler(_ => Json("rate limited", status));

            var result = await Create(handler).SendTransactionAsync(new byte[] { 1 });

            Assert.That(result.OutcomeUnknown, Is.False);
            Assert.That(result.Signature, Is.Null);
            Assert.That(result.Error, Does.Contain((int)status + "").And.Contain("not accepted"));
        }

        [Test]
        public async Task SendTransactionAsync_CallerCancellation_Propagates()
        {
            var handler = new StubHandler(_ => throw new TaskCanceledException());
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await Assert.ThrowsAsync<TaskCanceledException>(async () => await Create(handler).SendTransactionAsync(new byte[] { 1 }, cts.Token));
        }

        [Test]
        public async Task TryGetBalanceAsync_NonJsonBody_ReturnsNull()
        {
            var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>blocked</html>", Encoding.UTF8, "text/html") });

            Assert.That(await Create(handler).TryGetBalanceAsync("addr"), Is.Null);
        }

        [Test]
        public async Task SendTransactionAsync_NodeErrorResponse_IsAConfirmedRejectionNotOutcomeUnknown()
        {
            var handler = new StubHandler(_ => Json("{\"jsonrpc\":\"2.0\",\"error\":{\"code\":-32002,\"message\":\"nope\"},\"id\":1}"));

            var result = await Create(handler).SendTransactionAsync(new byte[] { 1 });

            Assert.That(result.OutcomeUnknown, Is.False);
        }

        [Test]
        public async Task SendTransactionAsync_PreflightUsesConfirmedCommitment()
        {
            var handler = new StubHandler(_ => Json("{\"jsonrpc\":\"2.0\",\"result\":\"sig\",\"id\":1}"));

            await Create(handler).SendTransactionAsync(new byte[] { 1 });

            Assert.That(handler.LastBody, Does.Contain("\"preflightCommitment\":\"confirmed\""));
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

        [Test]
        public void TryComputeSignature_MatchesBase58OfTheFirstSignature()
        {
            // 64 bytes with leading zeros: each leading zero byte must encode as a leading '1'.
            var signed = new byte[1 + 64 + 5];
            signed[0] = 1;
            signed[1] = 0;
            signed[2] = 0;
            signed[3] = 7;

            var signature = SolanaAddressValidator.TryComputeSignature(signed)!;

            Assert.That(signature, Does.StartWith("11"));
            Assert.That(signature, Is.EqualTo(Solnet.Wallet.Utilities.Encoders.Base58.EncodeData(signed.AsSpan(1, 64).ToArray())));
        }

        [Test]
        public void TryComputeSignature_TooShortOrMissing_ReturnsNull()
        {
            Assert.That(SolanaAddressValidator.TryComputeSignature(null), Is.Null);
            Assert.That(SolanaAddressValidator.TryComputeSignature(Array.Empty<byte>()), Is.Null);
            Assert.That(SolanaAddressValidator.TryComputeSignature(new byte[] { 1, 2, 3 }), Is.Null);
        }

        [Test]
        public void TryComputeSignature_MoreThanOneSignature_ReturnsNull()
        {
            var signed = new byte[200];
            signed[0] = 2;

            Assert.That(SolanaAddressValidator.TryComputeSignature(signed), Is.Null);
        }

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
