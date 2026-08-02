namespace BiatecMCP.Model
{
    /// <summary>App-wide settings, bound from the <c>App</c> configuration section.</summary>
    public class Configuration
    {
        /// <summary>This service's own public base URL (e.g. <c>https://mcp.biatec.io</c>).</summary>
        public string Host { get; set; } = "https://mcp.biatec.io";
    }
}
