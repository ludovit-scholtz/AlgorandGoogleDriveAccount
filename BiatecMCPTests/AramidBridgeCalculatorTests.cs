using System.Text.Json;
using BiatecMCP.BusinessLogic;
using BiatecMCP.Helper;

namespace BiatecMCPTests
{
    /// <summary>
    /// Covers <see cref="AramidBridgeCalculator"/> against the formulas/constraints published in Aramid
    /// Finance's AI-agent integration guide (fetched from
    /// https://raw.githubusercontent.com/AramidFinance/docs/refs/heads/main/docs/developers/ai-agent-integration.md
    /// while building this) - fee computation, decimals conversion, and the <c>aramid-transfer/v1:j</c>
    /// note format/validation. No network calls - <see cref="IAramidBridgeConfigProvider"/> (fetching
    /// Aramid's live config) is covered separately/mocked at the tool level.
    /// </summary>
    [TestFixture]
    public class AramidBridgeCalculatorTests
    {
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

        [Test]
        public void ComputeFeeAmount_NetworkFloorDominatesWhenRouteFeeIsNegligible()
        {
            var feeAlternative = new AramidFeeAlternative { SourcePercent = 0m, SourceConst = 0m };

            // Network floor: fee >= totalAmount - floor(totalAmount / 1.001).
            var fee = AramidBridgeCalculator.ComputeFeeAmount(1_001_000, feeAlternative);

            Assert.That(fee, Is.EqualTo(1_001_000 - (ulong)Math.Floor(1_001_000 / 1.001)));
        }

        [Test]
        public void ComputeFeeAmount_RouteMinimumDominatesWhenConfiguredHigherThanNetworkFloor()
        {
            // A route charging 5% clearly exceeds the ~0.0999% network floor.
            var feeAlternative = new AramidFeeAlternative { SourcePercent = 0.05m, SourceConst = 0m };

            var fee = AramidBridgeCalculator.ComputeFeeAmount(1_000_000, feeAlternative);

            // fee >= (totalAmount * sourcePercent + sourceConst) / (1 + sourcePercent), rounded up.
            var expectedRaw = (1_000_000m * 0.05m) / 1.05m;
            Assert.That(fee, Is.EqualTo((ulong)Math.Ceiling(expectedRaw)));
            Assert.That(fee, Is.GreaterThan(1_001_000 - (ulong)Math.Floor(1_001_000 / 1.001)));
        }

        [Test]
        public void ComputeFeeAmount_SourceConstAddsAFlatComponent()
        {
            var withoutConst = new AramidFeeAlternative { SourcePercent = 0.01m, SourceConst = 0m };
            var withConst = new AramidFeeAlternative { SourcePercent = 0.01m, SourceConst = 500m };

            var feeWithoutConst = AramidBridgeCalculator.ComputeFeeAmount(1_000_000, withoutConst);
            var feeWithConst = AramidBridgeCalculator.ComputeFeeAmount(1_000_000, withConst);

            Assert.That(feeWithConst, Is.GreaterThan(feeWithoutConst));
        }

        [Test]
        public void ComputeDestinationAmount_SameDecimals_ReturnsSourceAmountUnchanged()
        {
            var result = AramidBridgeCalculator.ComputeDestinationAmount(999_000, sourceDecimals: 6, destinationDecimals: 6);

            Assert.That(result, Is.EqualTo(999_000UL));
        }

        [Test]
        public void ComputeDestinationAmount_FewerDestinationDecimals_TruncatesDownNeverUp()
        {
            // 6 -> 2 decimals: divide by 10^4. 1,234,567 / 10,000 = 123.4567 -> must floor to 123, never 124.
            var result = AramidBridgeCalculator.ComputeDestinationAmount(1_234_567, sourceDecimals: 6, destinationDecimals: 2);

            Assert.That(result, Is.EqualTo(123UL));
        }

        [Test]
        public void ComputeDestinationAmount_MoreDestinationDecimals_MultipliesExactly()
        {
            // 2 -> 6 decimals: multiply by 10^4.
            var result = AramidBridgeCalculator.ComputeDestinationAmount(123, sourceDecimals: 2, destinationDecimals: 6);

            Assert.That(result, Is.EqualTo(1_230_000UL));
        }

        [Test]
        public void ValidateNote_Null_DoesNotThrow()
        {
            Assert.DoesNotThrow(() => AramidBridgeCalculator.ValidateNote(null));
        }

        [Test]
        public void ValidateNote_AllowedCharacters_DoesNotThrow()
        {
            Assert.DoesNotThrow(() => AramidBridgeCalculator.ValidateNote("Invoice-123, ref/2026 @biatec *5% +tax $10.50_ok"));
        }

        [Test]
        public void ValidateNote_TooLong_Throws()
        {
            var tooLong = new string('a', 51);

            Assert.Throws<ArgumentException>(() => AramidBridgeCalculator.ValidateNote(tooLong));
        }

        [Test]
        public void ValidateNote_DisallowedCharacter_Throws()
        {
            Assert.Throws<ArgumentException>(() => AramidBridgeCalculator.ValidateNote("not allowed: !"));
        }

        [Test]
        public void BuildTransferNote_StartsWithAramidPrefixAndEncodesAllFields()
        {
            var note = AramidBridgeCalculator.BuildTransferNote(
                destinationNetwork: 416101,
                destinationAddress: "VOIADDRESSHERE",
                destinationToken: "302189",
                feeAmount: 1000,
                sourceAmount: 999000,
                destinationAmount: 999000,
                note: "my-transfer");

            Assert.That(note, Does.StartWith("aramid-transfer/v1:j"));

            var json = note["aramid-transfer/v1:j".Length..];
            var payload = JsonSerializer.Deserialize<AramidTransferNotePayload>(json, JsonOptions)!;
            Assert.That(payload.DestinationNetwork, Is.EqualTo(416101));
            Assert.That(payload.DestinationAddress, Is.EqualTo("VOIADDRESSHERE"));
            Assert.That(payload.DestinationToken, Is.EqualTo("302189"));
            Assert.That(payload.FeeAmount, Is.EqualTo("1000"));
            Assert.That(payload.SourceAmount, Is.EqualTo("999000"));
            Assert.That(payload.DestinationAmount, Is.EqualTo("999000"));
            Assert.That(payload.Note, Is.EqualTo("my-transfer"));
        }

        [Test]
        public void BuildTransferNote_NoNote_EncodesEmptyStringNote()
        {
            var note = AramidBridgeCalculator.BuildTransferNote(416101, "ADDR", "0", 1, 2, 3, null);

            var json = note["aramid-transfer/v1:j".Length..];
            var payload = JsonSerializer.Deserialize<AramidTransferNotePayload>(json, JsonOptions)!;
            Assert.That(payload.Note, Is.Empty);
        }

        [Test]
        public void BuildTransferNote_InvalidNote_ThrowsBeforeBuildingAnything()
        {
            Assert.Throws<ArgumentException>(() =>
                AramidBridgeCalculator.BuildTransferNote(416101, "ADDR", "0", 1, 2, 3, "bad!"));
        }

        [Test]
        public void ResolveFeeAlternative_PicksTheOneActiveAtTheGivenRound()
        {
            var alternatives = new List<AramidFeeAlternative>
            {
                new() { ValidFrom = 0, ValidUntil = 1000, SourcePercent = 0.01m },
                new() { ValidFrom = 1001, ValidUntil = null, SourcePercent = 0.02m }
            };

            var resolved = AramidBridgeCalculator.ResolveFeeAlternative(alternatives, currentRound: 2000);

            Assert.That(resolved.SourcePercent, Is.EqualTo(0.02m));
        }

        [Test]
        public void ResolveFeeAlternative_NoAlternativeCoversTheCurrentRound_Throws()
        {
            var alternatives = new List<AramidFeeAlternative>
            {
                new() { ValidFrom = 0, ValidUntil = 1000, SourcePercent = 0.01m }
            };

            Assert.Throws<InvalidOperationException>(() =>
                AramidBridgeCalculator.ResolveFeeAlternative(alternatives, currentRound: 2000));
        }

        [Test]
        public void ResolveFeeAlternative_EmptyList_Throws()
        {
            Assert.Throws<InvalidOperationException>(() =>
                AramidBridgeCalculator.ResolveFeeAlternative(new List<AramidFeeAlternative>(), currentRound: 1));
        }
    }
}
