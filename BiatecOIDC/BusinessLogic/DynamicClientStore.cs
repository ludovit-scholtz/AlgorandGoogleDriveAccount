using System.Text.Json;
using BiatecOIDC.Model;
using StackExchange.Redis;

namespace BiatecOIDC.BusinessLogic
{
    /// <inheritdoc cref="IDynamicClientStore"/>
    public sealed class DynamicClientStore : IDynamicClientStore
    {
        private const string KeyPrefix = "oidc:dynclient:";

        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

        private readonly IConnectionMultiplexer _redis;

        public DynamicClientStore(IConnectionMultiplexer redis)
        {
            _redis = redis;
        }

        public async Task SaveAsync(JwtIssuerClientConfiguration client)
        {
            var json = JsonSerializer.Serialize(client, JsonOptions);
            // No expiry: a dynamically-registered public client (no secret, capped scopes - see
            // JwtIssuerConfiguration.DynamicClientRegistrationDefaultScopes) is cheap and low-risk to keep
            // around indefinitely, and an MCP client re-registering every time its cached client_id
            // happened to expire would be a worse experience than a small amount of stale Redis state. A
            // cleanup job is an easy future addition if this ever needs bounding.
            await _redis.GetDatabase().StringSetAsync(KeyPrefix + client.ClientId, json);
        }

        public async Task<JwtIssuerClientConfiguration?> GetAsync(string clientId)
        {
            var value = await _redis.GetDatabase().StringGetAsync(KeyPrefix + clientId);
            if (value.IsNullOrEmpty)
            {
                return null;
            }

            return JsonSerializer.Deserialize<JwtIssuerClientConfiguration>((string)value!, JsonOptions);
        }
    }
}
