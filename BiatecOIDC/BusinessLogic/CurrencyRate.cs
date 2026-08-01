namespace BiatecOIDC.BusinessLogic
{
    /// <summary>A currency the spending limit can be configured in, with its current rate against USD.</summary>
    public sealed class CurrencyRate
    {
        /// <summary>ISO 4217 currency code (e.g. <c>"EUR"</c>, <c>"CZK"</c>, <c>"USD"</c>).</summary>
        public required string Code { get; init; }

        /// <summary>Human-readable name (e.g. <c>"European Monetary Union euro"</c>), when known.</summary>
        public string? DisplayName { get; init; }

        /// <summary>How many USD 1 unit of <see cref="Code"/> is currently worth (e.g. ~1.08 for EUR).</summary>
        public required decimal UsdPerUnit { get; init; }
    }
}
