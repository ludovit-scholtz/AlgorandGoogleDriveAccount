using System.Text.Json;
using BiatecSelfCustodyCore.Model;

namespace BiatecMCPTests
{
    /// <summary>
    /// BiatecMCP and BiatecOIDC (via BiatecSelfCustodyCore) each define their own Solana transfer DTO (no shared compile-time
    /// code, per this repo's rule) and exchange it as JSON through <c>signTransaction</c>. This pins the contract: whatever
    /// serializer options either side uses, the property names are the same.
    /// </summary>
    [TestFixture]
    public class SolanaWireContractTests
    {
        private static readonly JsonSerializerOptions[] SerializerOptions =
        [
            new(),
            new(JsonSerializerDefaults.Web),
            new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }
        ];

        [Test]
        public void McpTransferDto_RoundTripsIntoTheSigningSidesDto_UnderAnySerializerOptions()
        {
            var sent = new BiatecMCP.Model.SolanaUnsignedTransfer
            {
                Recipient = "FoBNuzYQ4gpiP3jmevoiRVp3EnqBeXkB2HJ1LksSwYXJ",
                Lamports = 1_234_567,
                RecentBlockhash = "EkSnNWid2cvwEVnVx9aBqawnmiCNiDgp3gUdkDPTKN1N"
            };

            foreach (var writeOptions in SerializerOptions)
            {
                var json = JsonSerializer.SerializeToUtf8Bytes(sent, writeOptions);

                // BiatecOIDC deserializes with default options.
                var received = JsonSerializer.Deserialize<SolanaUnsignedTransfer>(json)!;

                Assert.That(received.Recipient, Is.EqualTo(sent.Recipient));
                Assert.That(received.Lamports, Is.EqualTo(sent.Lamports));
                Assert.That(received.RecentBlockhash, Is.EqualTo(sent.RecentBlockhash));
                Assert.That(received.Validate().Key, Is.EqualTo(sent.Recipient));
            }
        }

        [Test]
        public void TransferJson_UsesTheDocumentedPascalCasePropertyNames()
        {
            var json = JsonSerializer.Serialize(new SolanaUnsignedTransfer { Recipient = "r", Lamports = 1, RecentBlockhash = "b" }, new JsonSerializerOptions(JsonSerializerDefaults.Web));

            Assert.That(json, Is.EqualTo("{\"Recipient\":\"r\",\"Lamports\":1,\"RecentBlockhash\":\"b\"}"));
        }
    }
}
