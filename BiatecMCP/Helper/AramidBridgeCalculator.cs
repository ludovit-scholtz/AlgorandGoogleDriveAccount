using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using BiatecMCP.BusinessLogic;

namespace BiatecMCP.Helper
{
    /// <summary>
    /// Pure fee/amount/note-format math for the Aramid Finance bridge, per its published AI-agent
    /// integration guide (fetched from
    /// https://raw.githubusercontent.com/AramidFinance/docs/refs/heads/main/docs/developers/ai-agent-integration.md
    /// while this was built). No network calls, no key material - see <see cref="IAramidBridgeConfigProvider"/>
    /// for fetching the live route/fee configuration this operates on, and
    /// <c>BiatecMCP.MCP.BiatecMCP.CreateBridgeTransaction</c> for how the two are combined into an unsigned
    /// transaction.
    /// </summary>
    public static class AramidBridgeCalculator
    {
        private const string NotePrefix = "aramid-transfer/v1:j";

        /// <summary>Aramid's allowed note characters: letters, numbers, whitespace, and <c>. , - _ / @ * + $ %</c>.</summary>
        private static readonly Regex AllowedNoteCharacters = new(@"^[\p{L}\p{N}\s.,\-_/@*+$%]*$", RegexOptions.Compiled);

        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

        /// <summary>
        /// Computes the bridge fee to deduct from <paramref name="totalAmount"/> (the amount the sender
        /// pays), as the larger of two floors Aramid's guide specifies:
        /// <list type="bullet">
        /// <item>Network floor: <c>fee &gt;= totalAmount - floor(totalAmount / 1.001)</c>.</item>
        /// <item>Route minimum: <c>fee &gt;= sourceAmount * sourcePercent + sourceConst</c>, where
        /// <c>sourceAmount = totalAmount - fee</c> - solved algebraically as
        /// <c>fee = ceil((totalAmount * sourcePercent + sourceConst) / (1 + sourcePercent))</c> since the
        /// guide states the constraint in terms of the post-fee amount, not the total.</item>
        /// </list>
        /// Rounding is always up, per the guide's own warning that under-charging (and therefore
        /// over-crediting the destination) is what Aramid's own validators exist to reject - so an
        /// approximation here fails closed (transfer rejected) rather than causing any loss.
        /// </summary>
        public static ulong ComputeFeeAmount(ulong totalAmount, AramidFeeAlternative feeAlternative)
        {
            var networkFloorSourceAmount = (ulong)Math.Floor(totalAmount / 1.001);
            var networkFloorFee = totalAmount - networkFloorSourceAmount;

            var routeMinFeeRaw = (totalAmount * feeAlternative.SourcePercent + feeAlternative.SourceConst) / (1 + feeAlternative.SourcePercent);
            var routeMinFee = (ulong)Math.Ceiling(routeMinFeeRaw);

            return Math.Max(networkFloorFee, routeMinFee);
        }

        /// <summary>
        /// Converts <paramref name="sourceAmount"/> from the source asset's base units to the destination
        /// token's base units, always rounding down (per the guide: rounding up would let a recipient claim
        /// more value on the destination chain than was actually collateralized on the source chain).
        /// </summary>
        public static ulong ComputeDestinationAmount(ulong sourceAmount, int sourceDecimals, int destinationDecimals)
        {
            if (destinationDecimals == sourceDecimals)
            {
                return sourceAmount;
            }

            if (destinationDecimals > sourceDecimals)
            {
                var multiplier = (ulong)Math.Pow(10, destinationDecimals - sourceDecimals);
                return sourceAmount * multiplier;
            }

            var divisor = (ulong)Math.Pow(10, sourceDecimals - destinationDecimals);
            return sourceAmount / divisor; // integer division truncates toward zero == floor for non-negative values
        }

        /// <summary>
        /// Throws <see cref="ArgumentException"/> if <paramref name="note"/> violates Aramid's note-field
        /// rules (max 50 characters, letters/numbers/whitespace/<c>. , - _ / @ * + $ %</c> only). A
        /// <c>null</c>/empty note always passes - the field is optional.
        /// </summary>
        public static void ValidateNote(string? note)
        {
            if (string.IsNullOrEmpty(note))
            {
                return;
            }

            if (note.Length > 50)
            {
                throw new ArgumentException("note must be at most 50 characters.", nameof(note));
            }

            if (!AllowedNoteCharacters.IsMatch(note))
            {
                throw new ArgumentException("note contains characters Aramid's bridge does not allow (letters, numbers, whitespace, and . , - _ / @ * + $ % only).", nameof(note));
            }
        }

        /// <summary>
        /// Builds the <c>aramid-transfer/v1:j&lt;json&gt;</c> transaction note Aramid's bridge requires,
        /// with every amount encoded as a base-unit integer string (never a decimal, never scientific
        /// notation) per the guide. Validates <paramref name="note"/> first (see <see cref="ValidateNote"/>).
        /// </summary>
        public static string BuildTransferNote(long destinationNetwork, string destinationAddress, string destinationToken, ulong feeAmount, ulong sourceAmount, ulong destinationAmount, string? note)
        {
            ValidateNote(note);

            var payload = new AramidTransferNotePayload
            {
                DestinationNetwork = destinationNetwork,
                DestinationAddress = destinationAddress,
                DestinationToken = destinationToken,
                FeeAmount = feeAmount.ToString(CultureInfo.InvariantCulture),
                SourceAmount = sourceAmount.ToString(CultureInfo.InvariantCulture),
                DestinationAmount = destinationAmount.ToString(CultureInfo.InvariantCulture),
                Note = note ?? string.Empty
            };

            return NotePrefix + JsonSerializer.Serialize(payload, JsonOptions);
        }

        /// <summary>
        /// Picks whichever <paramref name="alternatives"/> entry is valid at <paramref name="currentRound"/>
        /// (a null <c>ValidFrom</c>/<c>ValidUntil</c> means unbounded on that side). Throws
        /// <see cref="InvalidOperationException"/> if none applies.
        /// </summary>
        public static AramidFeeAlternative ResolveFeeAlternative(IReadOnlyList<AramidFeeAlternative> alternatives, ulong currentRound)
        {
            var active = alternatives.FirstOrDefault(a =>
                (a.ValidFrom == null || currentRound >= a.ValidFrom) &&
                (a.ValidUntil == null || currentRound <= a.ValidUntil));

            if (active == null)
            {
                throw new InvalidOperationException("No Aramid fee configuration is currently active for this route.");
            }

            return active;
        }
    }
}
