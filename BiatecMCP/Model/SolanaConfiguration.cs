namespace BiatecMCP.Model
{
    /// <summary>Bound from <c>Solana</c> - where BiatecMCP reads Solana balances/blockhashes and broadcasts transactions.</summary>
    public class SolanaConfiguration
    {
        /// <summary>
        /// JSON-RPC endpoint of a Solana mainnet-beta node. The default is Solana's public endpoint, which is
        /// rate-limited - point this at a dedicated RPC provider for real traffic.
        /// </summary>
        public string RpcUrl { get; set; } = "https://api.mainnet-beta.solana.com";
    }
}
