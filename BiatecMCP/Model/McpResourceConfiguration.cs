namespace BiatecMCP.Model
{
    /// <summary>
    /// This MCP server's own identity as an OAuth 2.1 protected resource (RFC 9728/RFC 8707). Bound from
    /// the <c>Mcp</c> configuration section.
    /// </summary>
    public class McpResourceConfiguration
    {
        /// <summary>
        /// The canonical resource URI this server identifies itself as (e.g.
        /// <c>https://mcp.biatec.io/mcp</c>) - advertised in the RFC 9728 Protected Resource Metadata
        /// document, required as the <c>resource</c> parameter on every BiatecOIDC <c>/authorize</c>/
        /// <c>/token</c> call an MCP client makes, and validated as the required audience on every
        /// incoming bearer token.
        /// </summary>
        public string CanonicalResourceUri { get; set; } = "https://mcp.biatec.io/mcp";
    }
}
