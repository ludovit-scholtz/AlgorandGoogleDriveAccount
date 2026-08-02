namespace BiatecMCP.BusinessLogic
{
    /// <summary>A single DEX aggregator's quote for a swap - how much of the destination asset a given input would currently buy.</summary>
    public sealed class DexQuote
    {
        /// <summary>Which aggregator this quote came from (e.g. <c>"BiatecRouter"</c>, <c>"FolksRouter"</c>, <c>"HaystackRouter"</c>).</summary>
        public string ProviderName { get; set; } = string.Empty;

        /// <summary>How much of the destination asset (in its own base units) the requested input amount would currently buy.</summary>
        public long OutputAmount { get; set; }
    }
}
