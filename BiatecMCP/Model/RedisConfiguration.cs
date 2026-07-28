namespace BiatecMCP.Model
{
    /// <summary>Redis connection settings, bound from the <c>Redis</c> configuration section.</summary>
    public class RedisConfiguration
    {
        /// <summary>StackExchange.Redis connection string used for the distributed cache and session state.</summary>
        public string ConnectionString { get; set; } = "localhost:6379";
    }
}
