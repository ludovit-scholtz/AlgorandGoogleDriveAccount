namespace BiatecOIDC.Model
{
    public class ChainSummaryResponse
    {
        public string GenesisId { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string GenesisHash { get; set; } = string.Empty;
        public string AlgodApiAddress { get; set; } = string.Empty;
    }

    public class ChainsResponse
    {
        public List<ChainSummaryResponse> Chains { get; set; } = new();
    }
}
