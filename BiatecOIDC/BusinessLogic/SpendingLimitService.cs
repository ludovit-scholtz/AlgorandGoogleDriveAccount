using System.Text.Json;
using StackExchange.Redis;

namespace BiatecOIDC.BusinessLogic
{
    /// <inheritdoc cref="ISpendingLimitService"/>
    public class SpendingLimitService : ISpendingLimitService
    {
        private const string KeyPrefix = "oidc:spending-limit:";

        private readonly IConnectionMultiplexer _redis;
        private readonly ILogger<SpendingLimitService> _logger;

        private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        public SpendingLimitService(IConnectionMultiplexer redis, ILogger<SpendingLimitService> logger)
        {
            _redis = redis;
            _logger = logger;
        }

        public async Task<ulong> GetMaxAmountPerTransactionAsync(string email)
        {
            if (string.IsNullOrWhiteSpace(email))
            {
                throw new ArgumentException("Email is required.", nameof(email));
            }

            var json = await _redis.GetDatabase().StringGetAsync(BuildKey(email));
            if (json.IsNullOrEmpty)
            {
                return 0;
            }

            try
            {
                var record = JsonSerializer.Deserialize<SpendingLimitRecord>((string)json!, _jsonOptions);
                return record?.MaxAmountPerTransaction ?? 0;
            }
            catch (JsonException ex)
            {
                _logger.LogError(ex, "Corrupt spending-limit record for {Email}; treating as unbounded.", email);
                return 0;
            }
        }

        public async Task SetMaxAmountPerTransactionAsync(string email, ulong maxAmountPerTransaction)
        {
            if (string.IsNullOrWhiteSpace(email))
            {
                throw new ArgumentException("Email is required.", nameof(email));
            }

            var record = new SpendingLimitRecord
            {
                MaxAmountPerTransaction = maxAmountPerTransaction,
                UpdatedUtc = DateTimeOffset.UtcNow
            };

            await _redis.GetDatabase().StringSetAsync(BuildKey(email), JsonSerializer.Serialize(record, _jsonOptions));
        }

        private static string BuildKey(string email) => KeyPrefix + email.Trim().ToLowerInvariant();

        private sealed class SpendingLimitRecord
        {
            public ulong MaxAmountPerTransaction { get; set; }
            public DateTimeOffset UpdatedUtc { get; set; }
        }
    }
}
