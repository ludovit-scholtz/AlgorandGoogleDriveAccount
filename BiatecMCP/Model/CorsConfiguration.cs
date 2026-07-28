namespace BiatecMCP.Model
{
    /// <summary>Cross-origin request settings, bound from the <c>Cors</c> configuration section.</summary>
    public class CorsConfiguration
    {
        /// <summary>Origins allowed to call the API with credentials. Empty in Development means any origin is allowed (without credentials); empty in Production means no origin is allowed.</summary>
        public string[] AllowedOrigins { get; set; } = Array.Empty<string>();
    }
}
