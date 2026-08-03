using System.Numerics;
using BiatecOIDC.Helper;
using BiatecOIDC.Model;

namespace BiatecOIDCTests
{
    [TestFixture]
    public class EvmTransactionRequestParserTests
    {
        private static EvmTransactionRequest ValidLegacyRequest() => new()
        {
            ChainId = "1",
            Nonce = "5",
            To = "0x13f022d72158410433cbd66f5dd8bf6d2d0999c",
            Value = "1000000000000000000",
            Data = string.Empty,
            GasLimit = "21000",
            GasPrice = "20000000000"
        };

        [Test]
        public void Parse_ValidLegacyRequest_MapsAllFields()
        {
            var result = EvmTransactionRequestParser.Parse(ValidLegacyRequest());

            Assert.That(result.ChainId, Is.EqualTo((BigInteger)1));
            Assert.That(result.Nonce, Is.EqualTo((BigInteger)5));
            Assert.That(result.To, Is.EqualTo("0x13f022d72158410433cbd66f5dd8bf6d2d0999c"));
            Assert.That(result.Value, Is.EqualTo(System.Numerics.BigInteger.Parse("1000000000000000000")));
            Assert.That(result.GasLimit, Is.EqualTo((BigInteger)21000));
            Assert.That(result.GasPrice, Is.EqualTo((BigInteger?)20000000000));
            Assert.That(result.MaxFeePerGas, Is.Null);
            Assert.That(result.MaxPriorityFeePerGas, Is.Null);
        }

        [Test]
        public void Parse_ValidEip1559Request_MapsAllFields()
        {
            var request = ValidLegacyRequest();
            request.GasPrice = null;
            request.MaxFeePerGas = "30000000000";
            request.MaxPriorityFeePerGas = "1000000000";

            var result = EvmTransactionRequestParser.Parse(request);

            Assert.That(result.GasPrice, Is.Null);
            Assert.That(result.MaxFeePerGas, Is.EqualTo((BigInteger?)30000000000));
            Assert.That(result.MaxPriorityFeePerGas, Is.EqualTo((BigInteger?)1000000000));
        }

        [Test]
        public void Parse_HexNumericFields_ParsedAsUnsignedIntegers()
        {
            var request = ValidLegacyRequest();
            request.Value = "0xDE0B6B3A7640000"; // 1 ETH in wei
            request.GasPrice = "0x4A817C800"; // 20 gwei

            var result = EvmTransactionRequestParser.Parse(request);

            Assert.That(result.Value, Is.EqualTo(System.Numerics.BigInteger.Parse("1000000000000000000")));
            Assert.That(result.GasPrice, Is.EqualTo((BigInteger?)20000000000));
        }

        [Test]
        public void Parse_MissingTo_ThrowsFormatException()
        {
            var request = ValidLegacyRequest();
            request.To = string.Empty;

            Assert.That(() => EvmTransactionRequestParser.Parse(request), Throws.InstanceOf<FormatException>());
        }

        [Test]
        public void Parse_MissingChainId_ThrowsFormatException()
        {
            var request = ValidLegacyRequest();
            request.ChainId = string.Empty;

            Assert.That(() => EvmTransactionRequestParser.Parse(request), Throws.InstanceOf<FormatException>());
        }

        [Test]
        public void Parse_NeitherFeeShapeGiven_ThrowsFormatException()
        {
            var request = ValidLegacyRequest();
            request.GasPrice = null;

            Assert.That(() => EvmTransactionRequestParser.Parse(request), Throws.InstanceOf<FormatException>());
        }

        [Test]
        public void Parse_BothFeeShapesGiven_ThrowsFormatException()
        {
            var request = ValidLegacyRequest();
            request.MaxFeePerGas = "30000000000";
            request.MaxPriorityFeePerGas = "1000000000";

            Assert.That(() => EvmTransactionRequestParser.Parse(request), Throws.InstanceOf<FormatException>());
        }

        [Test]
        public void Parse_NonNumericField_ThrowsFormatException()
        {
            var request = ValidLegacyRequest();
            request.Nonce = "not-a-number";

            Assert.That(() => EvmTransactionRequestParser.Parse(request), Throws.InstanceOf<FormatException>());
        }

        [Test]
        public void Parse_NegativeField_ThrowsFormatException()
        {
            var request = ValidLegacyRequest();
            request.Value = "-1";

            Assert.That(() => EvmTransactionRequestParser.Parse(request), Throws.InstanceOf<FormatException>());
        }

        [Test]
        public void Parse_ValueOmitted_DefaultsToZero()
        {
            var request = ValidLegacyRequest();
            request.Value = string.Empty;

            var result = EvmTransactionRequestParser.Parse(request);

            Assert.That(result.Value, Is.EqualTo(System.Numerics.BigInteger.Zero));
        }
    }
}
