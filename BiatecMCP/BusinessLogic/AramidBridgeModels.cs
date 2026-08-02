namespace BiatecMCP.BusinessLogic
{
    /// <summary>
    /// Aramid Finance's live bridge configuration, fetched fresh before every bridge transaction is built
    /// (per Aramid's own integration guide: "do not cache indefinitely... re-validate immediately before
    /// transaction construction"). Shape mirrors Aramid's published <c>PublicConfigurationRoot</c>.
    /// </summary>
    public sealed class AramidConfigRoot
    {
        public string? Hash { get; set; }

        /// <summary>Every chain Aramid bridges to/from, keyed by chain id (as a string).</summary>
        public Dictionary<string, AramidChainItem> Chains { get; set; } = new();

        /// <summary>
        /// Route/fee lookup: <c>Chains2Tokens[sourceChainId][destinationChainId][sourceTokenId][destinationTokenId]</c>.
        /// Absence at any level means that route doesn't exist.
        /// </summary>
        public Dictionary<string, Dictionary<string, Dictionary<string, Dictionary<string, AramidMappingItem>>>> Chains2Tokens { get; set; } = new();
    }

    /// <summary>One chain Aramid bridges to/from.</summary>
    public sealed class AramidChainItem
    {
        public long ChainId { get; set; }
        public string Name { get; set; } = string.Empty;

        /// <summary><c>"algo"</c>, <c>"eth"</c>, or <c>"near"</c>.</summary>
        public string Type { get; set; } = string.Empty;

        /// <summary>The bridge's own deposit/multisig address on this chain - transfers are sent here, never directly to the recipient.</summary>
        public string Address { get; set; } = string.Empty;

        public Dictionary<string, AramidTokenItem> Tokens { get; set; } = new();
        public int? ConfirmationCount { get; set; }
    }

    /// <summary>One token Aramid recognizes on a given chain.</summary>
    public sealed class AramidTokenItem
    {
        public int? Decimals { get; set; }
    }

    /// <summary>One source/destination token pair's route, with its fee schedule.</summary>
    public sealed class AramidMappingItem
    {
        public List<AramidFeeAlternative> FeeAlternatives { get; set; } = new();
    }

    /// <summary>One fee schedule entry for a route, valid for a specific round range.</summary>
    public sealed class AramidFeeAlternative
    {
        public ulong? ValidFrom { get; set; }
        public ulong? ValidUntil { get; set; }
        public decimal MinimumAmount { get; set; }
        public decimal MaximumAmount { get; set; }
        public decimal SourceConst { get; set; }
        public decimal SourcePercent { get; set; }
        public decimal DestinationConst { get; set; }
        public decimal DestinationPercent { get; set; }
    }

    /// <summary>The JSON payload embedded in an <c>aramid-transfer/v1:j</c> transaction note.</summary>
    public sealed class AramidTransferNotePayload
    {
        public long DestinationNetwork { get; set; }
        public string DestinationAddress { get; set; } = string.Empty;
        public string DestinationToken { get; set; } = string.Empty;
        public string FeeAmount { get; set; } = string.Empty;
        public string SourceAmount { get; set; } = string.Empty;
        public string DestinationAmount { get; set; } = string.Empty;
        public string Note { get; set; } = string.Empty;
    }
}
