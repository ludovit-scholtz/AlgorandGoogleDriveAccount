namespace BiatecMCP.Model
{
    /// <summary>Bound from <c>Solana</c> - where BiatecMCP reads Solana balances/blockhashes and broadcasts transactions.</summary>
    public class SolanaConfiguration
    {
        /// <summary>
        /// JSON-RPC endpoint of a Solana mainnet-beta node. The default is Solana's public endpoint, which is
        /// rate-limited - point this at a dedicated RPC provider for real traffic. It must be a <b>mainnet-beta</b> node:
        /// BiatecOIDC prices and limits every Solana transfer as mainnet SOL (the only supported network, <c>solana</c>), so
        /// pointing this at devnet/testnet would build and broadcast on the wrong cluster while the spending limit still counts
        /// mainnet value, and nothing detects the mismatch.
        /// </summary>
        public string RpcUrl { get; set; } = "https://api.mainnet-beta.solana.com";
    }
}
